[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeSeed,

    [Parameter(Mandatory)]
    [string]$WebView2FixedRuntime,

    [string]$DotnetExecutable = 'dotnet',

    [string]$OutputFile = (Join-Path $PSScriptRoot '..\artifacts\DeepSeekHarnessDesktop-Setup.exe'),

    [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter()][string[]]$Arguments = @(),
        [Parameter()][string]$WorkingDirectory
    )

    $previousLocation = Get-Location
    try {
        if ($WorkingDirectory) {
            Set-Location -LiteralPath $WorkingDirectory
        }

        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code $($LASTEXITCODE): $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        if ($WorkingDirectory) {
            Set-Location -LiteralPath $previousLocation
        }
    }
}

function Copy-Tree {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /XJ /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Unable to copy $Source to $Destination (robocopy exit $LASTEXITCODE)."
    }
}

function Test-SeedRuntime {
    param([Parameter(Mandatory)][string]$RuntimeRoot)

    $manifestPath = Join-Path $RuntimeRoot 'current.json'
    $nodePath = Join-Path $RuntimeRoot 'node\node.exe'
    if (-not (Test-Path -LiteralPath $manifestPath) -or -not (Test-Path -LiteralPath $nodePath)) {
        return $false
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $releasePath = Join-Path $RuntimeRoot ([string]$manifest.releaseDirectory)
        $entryPath = Join-Path $releasePath ([string]$manifest.entryPoint)
        return Test-Path -LiteralPath $entryPath
    }
    catch {
        return $false
    }
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeSeedPath = [IO.Path]::GetFullPath($RuntimeSeed)
$webViewPath = [IO.Path]::GetFullPath($WebView2FixedRuntime)
$setupPath = [IO.Path]::GetFullPath($OutputFile)

if (-not (Test-SeedRuntime $runtimeSeedPath)) {
    throw "RuntimeSeed is not a valid portable Harness runtime: $runtimeSeedPath"
}
if (-not (Test-Path -LiteralPath (Join-Path $webViewPath 'msedgewebview2.exe'))) {
    throw "WebView2FixedRuntime does not contain msedgewebview2.exe: $webViewPath"
}
if (Test-Path -LiteralPath $setupPath) {
    throw "Refusing to overwrite an existing setup file: $setupPath"
}

$runtimeLinks = Get-ChildItem -LiteralPath $runtimeSeedPath -Recurse -Force -ErrorAction Stop |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
    Select-Object -First 1
if ($runtimeLinks) {
    throw 'RuntimeSeed contains symbolic links or junctions. Rebuild it with New-RuntimeSeed.ps1 before creating a cross-computer offline installer.'
}

$dotnet = (Get-Command $DotnetExecutable -ErrorAction Stop).Source
$iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'
$tar = Join-Path $env:WINDIR 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $iexpress) -or -not (Test-Path -LiteralPath $tar)) {
    throw 'Windows IExpress and tar.exe are required to build the one-file setup.'
}

$setupParent = Split-Path -LiteralPath $setupPath -Parent
New-Item -ItemType Directory -Path $setupParent -Force | Out-Null
$stage = Join-Path $env:TEMP ('DeepSeekHarnessDesktop-Setup-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$payload = Join-Path $stage 'payload'
$sfx = Join-Path $stage 'sfx'
New-Item -ItemType Directory -Path $publish, $payload, $sfx -Force | Out-Null

try {
    Invoke-Checked $dotnet @('restore', (Join-Path $projectRoot 'DeepSeekHarnessDesktop.csproj')) $projectRoot
    Invoke-Checked $dotnet @(
        'publish', (Join-Path $projectRoot 'DeepSeekHarnessDesktop.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishReadyToRun=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publish) $projectRoot

    if (-not (Test-Path -LiteralPath (Join-Path $publish 'DeepSeekHarnessDesktop.exe'))) {
        throw 'Desktop publish did not create DeepSeekHarnessDesktop.exe.'
    }

    Copy-Tree $publish (Join-Path $payload 'app')
    Copy-Tree $runtimeSeedPath (Join-Path $payload 'runtime')
    Copy-Tree $webViewPath (Join-Path $payload 'webview2')

    $runtimeManifest = Get-Content -LiteralPath (Join-Path $runtimeSeedPath 'current.json') -Raw | ConvertFrom-Json
    $payloadManifest = [ordered]@{
        schemaVersion = 1
        desktopVersion = (Get-Item -LiteralPath (Join-Path $publish 'DeepSeekHarnessDesktop.exe')).VersionInfo.FileVersion
        harnessVersion = [string]$runtimeManifest.version
        webView2Included = $true
        createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        (Join-Path $payload 'install-manifest.json'),
        $payloadManifest + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    $forbidden = Get-ChildItem -LiteralPath $payload -Recurse -Force -File |
        Where-Object { $_.Name -match '(^\.credentials\.yaml$|credential|api[_-]?key)' }
    if ($forbidden) {
        throw 'Payload contains a credential-like file. Refusing to create a public-distribution installer.'
    }

    $archive = Join-Path $sfx 'payload.tar.gz'
    Invoke-Checked $tar @('-czf', $archive, '-C', $stage, 'payload') $stage
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        (Join-Path $sfx 'payload.sha256'),
        "$hash  payload.tar.gz" + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-DeepSeekHarnessDesktop.ps1') -Destination (Join-Path $sfx 'Install-DeepSeekHarnessDesktop.ps1')
    $launcher = @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-DeepSeekHarnessDesktop.ps1"
exit /b %ERRORLEVEL%
'@
    [IO.File]::WriteAllText(
        (Join-Path $sfx 'Install.cmd'),
        $launcher,
        [Text.ASCIIEncoding]::new())

    $sedPath = Join-Path $stage 'setup.sed'
    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_UseLongFileName=1
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=DeepSeek Harness Desktop 安装完成。
TargetName=$setupPath
FriendlyName=DeepSeek Harness Desktop Setup
AppLaunched=Install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=0
[Strings]
FILE0="Install.cmd"
FILE1="Install-DeepSeekHarnessDesktop.ps1"
FILE2="payload.tar.gz"
FILE3="payload.sha256"
[SourceFiles]
SourceFiles0=$sfx\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
"@
    [IO.File]::WriteAllText($sedPath, $sed, [Text.UTF8Encoding]::new($false))

    Invoke-Checked $iexpress @('/N', $sedPath) $stage
    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw 'IExpress did not create the setup executable.'
    }

    Write-Host "Offline setup created: $setupPath"
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash)"
}
finally {
    if (-not $KeepStaging -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    elseif ($KeepStaging) {
        Write-Host "Staging retained: $stage"
    }
}
