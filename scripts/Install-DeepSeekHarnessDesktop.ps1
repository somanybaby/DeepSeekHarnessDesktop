[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-SeedRuntime {
    param([Parameter(Mandatory)][string]$RuntimeRoot)

    $manifestPath = Join-Path $RuntimeRoot 'current.json'
    $nodePath = Join-Path $RuntimeRoot 'node\node.exe'
    if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $nodePath)) {
        return $false
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $release = [string]$manifest.releaseDirectory
        $entry = [string]$manifest.entryPoint
        if ([string]::IsNullOrWhiteSpace($release) -or [string]::IsNullOrWhiteSpace($entry)) {
            return $false
        }

        $releasePath = [IO.Path]::GetFullPath((Join-Path $RuntimeRoot $release))
        $rootPath = [IO.Path]::GetFullPath($RuntimeRoot).TrimEnd('\') + '\'
        return $releasePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath (Join-Path $releasePath $entry))
    }
    catch {
        return $false
    }
}

function Test-DesktopProcessRunning {
    param([Parameter(Mandatory)][string]$InstallRoot)

    $normalizedRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    return @(Get-CimInstance Win32_Process -Filter "Name='DeepSeekHarnessDesktop.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and $_.ExecutablePath.StartsWith(
                $normalizedRoot,
                [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
}

function Move-ReplaceDirectory {
    param(
        [Parameter(Mandatory)][string]$Incoming,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$BackupPrefix
    )

    $backup = $null
    if (Test-Path -LiteralPath $Destination) {
        $backup = "$Destination.$BackupPrefix-$(Get-Date -Format 'yyyyMMddHHmmss')"
        Move-Item -LiteralPath $Destination -Destination $backup
    }

    try {
        Move-Item -LiteralPath $Incoming -Destination $Destination
        return $backup
    }
    catch {
        if ($backup -and -not (Test-Path -LiteralPath $Destination)) {
            Move-Item -LiteralPath $backup -Destination $Destination
        }
        throw
    }
}

$extractRoot = $PSScriptRoot
$archive = Join-Path $extractRoot 'payload.tar.gz'
$hashFile = Join-Path $extractRoot 'payload.sha256'
if (-not (Test-Path -LiteralPath $archive) -or -not (Test-Path -LiteralPath $hashFile)) {
    throw 'Installer payload is incomplete.'
}

$expectedHash = ((Get-Content -LiteralPath $hashFile -Raw).Trim() -split '\s+')[0]
$actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Installer payload integrity check failed.'
}

$installRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))
    'DeepSeekHarnessDesktop'
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null

if (Test-DesktopProcessRunning $installRoot) {
    throw '请先从右下角托盘选择“退出”，关闭正在运行的 DeepSeek Harness Desktop，然后重新运行安装程序。'
}

$temporaryRoot = Join-Path $installRoot ('.setup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

try {
    $tar = Join-Path $env:WINDIR 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tar)) {
        $tarCommand = Get-Command tar.exe -ErrorAction Stop
        $tar = $tarCommand.Source
    }

    & $tar -xzf $archive -C $temporaryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to extract installer payload (tar exit $LASTEXITCODE)."
    }

    $payloadRoot = Join-Path $temporaryRoot 'payload'
    $manifestPath = Join-Path $payloadRoot 'install-manifest.json'
    $incomingApp = Join-Path $payloadRoot 'app'
    $incomingRuntime = Join-Path $payloadRoot 'runtime'
    if (-not (Test-Path -LiteralPath $manifestPath) -or
        -not (Test-Path -LiteralPath (Join-Path $incomingApp 'DeepSeekHarnessDesktop.exe')) -or
        -not (Test-SeedRuntime $incomingRuntime)) {
        throw 'Installer payload failed structural validation.'
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1) {
        throw 'Installer payload uses an unsupported schema.'
    }

    if ($manifest.webView2Included -eq $true) {
        $incomingWebView = Join-Path $payloadRoot 'webview2'
        if (-not (Test-Path -LiteralPath (Join-Path $incomingWebView 'msedgewebview2.exe'))) {
            throw 'The bundled WebView2 runtime is incomplete.'
        }

        Move-Item -LiteralPath $incomingWebView -Destination (Join-Path $incomingApp 'webview2')
    }

    $appTarget = Join-Path $installRoot 'app'
    $appBackup = Move-ReplaceDirectory $incomingApp $appTarget 'previous'

    $runtimeTarget = Join-Path $installRoot 'runtime'
    if (-not (Test-SeedRuntime $runtimeTarget)) {
        $runtimeBackup = Move-ReplaceDirectory $incomingRuntime $runtimeTarget 'previous'
    }

    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    $shortcutPath = Join-Path $desktop 'DeepSeek Harness Desktop.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $appTarget 'DeepSeekHarnessDesktop.exe'
    $shortcut.WorkingDirectory = $appTarget
    $shortcut.IconLocation = "$($shortcut.TargetPath),0"
    $shortcut.Description = 'DeepSeek Harness Desktop'
    $shortcut.Save()

    # Credentials remain outside the installation tree. On a new Windows user
    # account no ~/.dsh credential file exists, so the official API settings
    # page opens with an empty configuration by design.
    Start-Process -FilePath $shortcut.TargetPath -WorkingDirectory $appTarget
    Write-Host 'Installation complete. API configuration is intentionally empty on first install.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
