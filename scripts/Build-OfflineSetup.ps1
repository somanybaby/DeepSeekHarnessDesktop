[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$RuntimeSeed,

    [Parameter(Mandatory)]
    [string]$WebView2FixedRuntime,

    [string]$DotnetExecutable = 'dotnet',

    [string]$OutputFile = (Join-Path $PSScriptRoot '..\artifacts\DeepSeekHarnessDesktop-Setup.exe'),

    [switch]$SkipRuntimeLinkValidation,

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
    & robocopy $Source $Destination /E /MT:32 /COPY:DAT /DCOPY:DAT /R:2 /W:1 /XJ /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Unable to copy $Source to $Destination (robocopy exit $LASTEXITCODE)."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $stream = [IO.File]::OpenRead($LiteralPath)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '')
    }
    finally {
        $hasher.Dispose()
        $stream.Dispose()
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

if (-not $SkipRuntimeLinkValidation) {
    $runtimeLinks = Get-ChildItem -LiteralPath $runtimeSeedPath -Recurse -Force -ErrorAction Stop |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
        Select-Object -First 1
    if ($runtimeLinks) {
        throw 'RuntimeSeed contains symbolic links or junctions. Rebuild it with Materialize-RuntimeSeed.ps1 before creating a cross-computer offline installer.'
    }
}

$dotnet = (Get-Command $DotnetExecutable -ErrorAction Stop).Source
$tar = Join-Path $env:WINDIR 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar)) {
    throw 'Windows tar.exe is required to build the one-file setup.'
}

$setupParent = [IO.Path]::GetDirectoryName($setupPath)
New-Item -ItemType Directory -Path $setupParent -Force | Out-Null
$stage = Join-Path $env:TEMP ('DeepSeekHarnessDesktop-Setup-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$payload = Join-Path $stage 'payload'
$sfx = Join-Path $stage 'sfx'
$setupPublish = Join-Path $stage 'setup-publish'
New-Item -ItemType Directory -Path $publish, $payload, $sfx, $setupPublish -Force | Out-Null

try {
    Invoke-Checked -FilePath $dotnet -Arguments @('restore', (Join-Path $projectRoot 'DeepSeekHarnessDesktop.csproj')) -WorkingDirectory $projectRoot
    Invoke-Checked -FilePath $dotnet -Arguments @(
        'publish', (Join-Path $projectRoot 'DeepSeekHarnessDesktop.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishReadyToRun=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publish) -WorkingDirectory $projectRoot

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
        Where-Object {
            $_.Name -ieq '.credentials.yaml' -or
            $_.FullName -match '\\.dsh\\'
        }
    if ($forbidden) {
        throw 'Payload contains a credential-like file. Refusing to create a public-distribution installer.'
    }

    $archive = Join-Path $sfx 'payload.tar.gz'
    Invoke-Checked -FilePath $tar -Arguments @('-czf', $archive, '-C', $stage, 'payload') -WorkingDirectory $stage
    $bootstrapperProject = Join-Path $projectRoot 'installer\SetupBootstrapper\DeepSeekHarnessDesktopSetup.csproj'
    if (-not (Test-Path -LiteralPath $bootstrapperProject)) {
        throw "Bootstrapper project is missing: $bootstrapperProject"
    }

    Invoke-Checked -FilePath $dotnet -Arguments @(
        'publish', $bootstrapperProject,
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishReadyToRun=false',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:PayloadPath=$archive", '-o', $setupPublish) -WorkingDirectory $projectRoot

    $generatedSetup = Join-Path $setupPublish 'DeepSeekHarnessDesktopSetup.exe'
    if (-not (Test-Path -LiteralPath $generatedSetup)) {
        throw 'The self-contained bootstrapper publish did not create its setup executable.'
    }
    Copy-Item -LiteralPath $generatedSetup -Destination $setupPath

    Write-Host "Offline setup created: $setupPath"
    Write-Host "SHA-256: $(Get-Sha256 -LiteralPath $setupPath)"
}
finally {
    if (-not $KeepStaging -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    elseif ($KeepStaging) {
        Write-Host "Staging retained: $stage"
    }
}
