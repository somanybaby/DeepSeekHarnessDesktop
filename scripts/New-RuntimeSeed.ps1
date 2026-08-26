[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [string]$HarnessVersion = '0.1.1-rc.2',

    [string]$NodeVersion = '22.23.2',

    [string]$NodeSource
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
        throw "Unable to copy runtime files from $Source to $Destination (robocopy exit $LASTEXITCODE)."
    }
}

$seedRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $seedRoot) {
    $existing = Get-ChildItem -LiteralPath $seedRoot -Force | Select-Object -First 1
    if ($null -ne $existing) {
        throw "OutputRoot must be empty to avoid overwriting a known-good runtime: $seedRoot"
    }
}

New-Item -ItemType Directory -Path $seedRoot -Force | Out-Null
$workRoot = Join-Path $seedRoot ('.seed-work-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workRoot | Out-Null

try {
    $nodeDestination = Join-Path $seedRoot 'node'
    if ($NodeSource) {
        $nodeSourceFull = [IO.Path]::GetFullPath($NodeSource)
        if (-not (Test-Path -LiteralPath (Join-Path $nodeSourceFull 'node.exe'))) {
            throw "NodeSource does not contain node.exe: $nodeSourceFull"
        }

        Copy-Tree $nodeSourceFull $nodeDestination
    }
    else {
        $nodeArchiveName = "node-v$NodeVersion-win-x64.zip"
        $nodeBaseUrl = "https://nodejs.org/dist/v$NodeVersion"
        $archivePath = Join-Path $workRoot $nodeArchiveName
        $checksumsPath = Join-Path $workRoot 'SHASUMS256.txt'

        Invoke-WebRequest -Uri "$nodeBaseUrl/$nodeArchiveName" -OutFile $archivePath
        Invoke-WebRequest -Uri "$nodeBaseUrl/SHASUMS256.txt" -OutFile $checksumsPath
        $expectedHash = (Select-String -LiteralPath $checksumsPath -Pattern ("\\s" + [regex]::Escape($nodeArchiveName) + '$') | Select-Object -First 1).Line.Split()[0]
        if ([string]::IsNullOrWhiteSpace($expectedHash)) {
            throw "Node.js checksum was not found for $nodeArchiveName."
        }

        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Downloaded Node.js archive did not match the official SHA-256 checksum.'
        }

        $extractRoot = Join-Path $workRoot 'node-extracted'
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot
        $nodeFolder = Join-Path $extractRoot ("node-v$NodeVersion-win-x64")
        if (-not (Test-Path -LiteralPath (Join-Path $nodeFolder 'node.exe'))) {
            throw 'Expanded Node.js archive has an unexpected layout.'
        }

        Move-Item -LiteralPath $nodeFolder -Destination $nodeDestination
    }

    $nodeExecutable = Join-Path $nodeDestination 'node.exe'
    $npmCli = Join-Path $nodeDestination 'node_modules\npm\bin\npm-cli.js'
    if (-not (Test-Path -LiteralPath $npmCli)) {
        throw 'The selected Node.js runtime does not include npm-cli.js.'
    }

    $releaseDirectory = Join-Path $seedRoot (Join-Path 'releases' $HarnessVersion)
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    $package = [ordered]@{
        name = 'deepseek-harness-desktop-runtime'
        version = '1.0.0'
        private = $true
        dependencies = [ordered]@{
            '@deepseek-ai/dsh' = $HarnessVersion
            pnpm = '11.7.0'
        }
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        (Join-Path $releaseDirectory 'package.json'),
        $package + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    Invoke-Checked $nodeExecutable @(
        $npmCli, 'install', '--omit=dev', '--no-audit', '--no-fund', '--save-exact') $releaseDirectory

    $cliEntry = Join-Path $releaseDirectory 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    $pnpmEntry = Join-Path $releaseDirectory 'node_modules\pnpm\bin\pnpm.cjs'
    if (-not (Test-Path -LiteralPath $cliEntry) -or -not (Test-Path -LiteralPath $pnpmEntry)) {
        throw 'The npm installation did not produce the expected Harness CLI and pnpm files.'
    }

    Invoke-Checked $nodeExecutable @($cliEntry, '--version') $releaseDirectory

    $manifest = [ordered]@{
        schemaVersion = 1
        version = $HarnessVersion
        releaseDirectory = "releases/$HarnessVersion"
        entryPoint = 'node_modules/@deepseek-ai/dsh/lib/bin.js'
        toolsDirectory = 'node_modules/.bin'
        activatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText(
        (Join-Path $seedRoot 'current.json'),
        $manifest + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))

    $forbidden = Get-ChildItem -LiteralPath $seedRoot -Recurse -Force -File |
        Where-Object { $_.Name -match '(^\.credentials\.yaml$|credential|api[_-]?key)' }
    if ($forbidden) {
        throw 'Runtime seed unexpectedly contains a credential-like file. Refusing to package it.'
    }

    Write-Host "Runtime seed created: $seedRoot"
    Write-Host "Harness version: $HarnessVersion"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
