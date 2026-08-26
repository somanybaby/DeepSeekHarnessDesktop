[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRuntime,

    [Parameter(Mandatory)]
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sourceRoot = [IO.Path]::GetFullPath($SourceRuntime)
$seedRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'current.json')) -or
    -not (Test-Path -LiteralPath (Join-Path $sourceRoot 'node\node.exe'))) {
    throw "SourceRuntime is not a valid desktop runtime: $sourceRoot"
}

if (Test-Path -LiteralPath $seedRoot) {
    $existing = Get-ChildItem -LiteralPath $seedRoot -Force | Select-Object -First 1
    if ($null -ne $existing) {
        throw "OutputRoot must be empty to avoid overwriting a known-good runtime: $seedRoot"
    }
}

$sourceManifest = Get-Content -LiteralPath (Join-Path $sourceRoot 'current.json') -Raw | ConvertFrom-Json
$version = [string]$sourceManifest.version
$releaseRelative = [string]$sourceManifest.releaseDirectory
$entryRelative = [string]$sourceManifest.entryPoint
if ([string]::IsNullOrWhiteSpace($version) -or
    [string]::IsNullOrWhiteSpace($releaseRelative) -or
    [string]::IsNullOrWhiteSpace($entryRelative)) {
    throw 'Source runtime manifest is incomplete.'
}

$sourceRelease = [IO.Path]::GetFullPath((Join-Path $sourceRoot $releaseRelative))
$sourceRootPrefix = $sourceRoot.TrimEnd('\') + '\'
if (-not $sourceRelease.StartsWith($sourceRootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $sourceRelease $entryRelative))) {
    throw 'Source runtime manifest points outside its release tree or the CLI entry is missing.'
}

New-Item -ItemType Directory -Path $seedRoot -Force | Out-Null
Write-Host 'Copying Node.js runtime...'
Copy-Item -LiteralPath (Join-Path $sourceRoot 'node') -Destination (Join-Path $seedRoot 'node') -Recurse -Force
Write-Host 'Materializing Harness packages (this intentionally resolves pnpm symbolic links)...'
New-Item -ItemType Directory -Path (Join-Path $seedRoot 'releases') -Force | Out-Null
Copy-Item -LiteralPath $sourceRelease -Destination (Join-Path $seedRoot (Join-Path 'releases' $version)) -Recurse -Force

$releaseDirectory = Join-Path $seedRoot (Join-Path 'releases' $version)
$packagePath = Join-Path $releaseDirectory 'node_modules\@deepseek-ai\dsh\package.json'
if (-not (Test-Path -LiteralPath $packagePath)) {
    throw 'Materialized runtime does not contain the Harness package.'
}

$package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
if ([string]$package.version -ne $version) {
    throw "Materialized package version ($($package.version)) does not match runtime manifest ($version)."
}

$links = Get-ChildItem -LiteralPath $seedRoot -Recurse -Force |
    Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
    Select-Object -First 1
if ($links) {
    throw "Materialization left a symbolic link or junction: $($links.FullName)"
}

$forbidden = Get-ChildItem -LiteralPath $seedRoot -Recurse -Force -File |
    Where-Object {
        $_.Name -ieq '.credentials.yaml' -or
        $_.FullName -match '\\.dsh\\'
    }
if ($forbidden) {
    throw 'Materialized runtime unexpectedly contains a credential-like file. Refusing to package it.'
}

$manifest = [ordered]@{
    schemaVersion = 1
    version = $version
    releaseDirectory = "releases/$version"
    entryPoint = $entryRelative
    toolsDirectory = if ($sourceManifest.toolsDirectory) { [string]$sourceManifest.toolsDirectory } else { 'node_modules/.bin' }
    activatedAt = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText(
    (Join-Path $seedRoot 'current.json'),
    $manifest + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

Write-Host "Materialized runtime seed created: $seedRoot"
Write-Host "Harness version: $version"
