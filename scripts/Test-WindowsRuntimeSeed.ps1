[CmdletBinding()]
param([Parameter(Mandatory)][string]$RuntimeSeed)
$ErrorActionPreference = 'Stop'
$seedRoot = [IO.Path]::GetFullPath($RuntimeSeed)
$manifest = Get-Content -LiteralPath (Join-Path $seedRoot 'current.json') -Raw | ConvertFrom-Json
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $seedRoot $manifest.releaseDirectory))
if (-not $releaseRoot.StartsWith($seedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release escapes seed.' }
$report = Get-Content -LiteralPath (Join-Path $releaseRoot 'windows-package-report.json') -Raw | ConvertFrom-Json
if ($report.target -ne 'win32-x64') { throw 'Only a reviewed Windows x64 seed may be distributed.' }
$files = @(Get-ChildItem -LiteralPath $seedRoot -Recurse -File -Force)
$private = $files | Where-Object { $_.Name -eq '.credentials.yaml' -or $_.FullName -match '\\(?:\.dsh|test-user-data|pnpm-store|\.staging)\\' } | Select-Object -First 1
if ($private) { throw 'Runtime seed contains private data or an installation cache.' }
$foreign = $files | Where-Object {
    $_.Name -match '\.(so(\.\d+)*)$|\.dylib$' -or
    ($_.Extension -in '.exe','.dll','.node' -and $_.FullName -match '(darwin|linux|freebsd|android|win32-arm64|win32-ia32|win10-arm64)')
} | Select-Object -First 1
if ($foreign) { throw "Non-Windows-x64 binary found: $($foreign.FullName)" }
$virtualStore = Join-Path $releaseRoot 'node_modules\.pnpm'
if ((Test-Path -LiteralPath $virtualStore) -and (Get-ChildItem -LiteralPath $virtualStore -Directory -Force | Select-Object -First 1)) {
    throw 'Materialized pnpm duplicate packages must not be shipped.'
}
Write-Host "PASS Windows x64 seed: $($files.Count) files; no foreign native binaries, credentials or package caches."
