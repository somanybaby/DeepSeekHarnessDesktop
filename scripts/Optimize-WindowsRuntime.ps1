[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\')
$modulesRoot = Join-Path $releaseRoot 'node_modules'
if (-not (Test-Path -LiteralPath (Join-Path $modulesRoot '@deepseek-ai\dsh\package.json'))) { throw 'Not a Harness release.' }
$policy = Get-Content -LiteralPath (Join-Path $releaseRoot 'pnpm-workspace.yaml') -Raw
if ($policy -notmatch 'nodeLinker: hoisted') { throw 'Only optimize freshly built hoisted runtime seeds.' }
$removedBytes = 0L
$removedPaths = [Collections.Generic.List[string]]::new()
function Remove-PlatformDirectory([string]$Target) {
    $resolved = (Resolve-Path -LiteralPath $Target -ErrorAction Stop).ProviderPath.TrimEnd('\')
    if (-not $resolved.StartsWith($modulesRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside node_modules.' }
    if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to follow a link.' }
    $size = (Get-ChildItem -LiteralPath $resolved -Recurse -Force -File | Measure-Object Length -Sum).Sum
    $script:removedBytes += [long]$size
    $script:removedPaths.Add($resolved.Substring($releaseRoot.Length+1))
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
# pnpm selects Windows-only optional packages. Some upstream packages also ship
# embedded prebuilds for every platform; prune only reviewed binary directories.
$packageFiles = @(Get-ChildItem -LiteralPath $modulesRoot -Recurse -File -Filter package.json)
foreach ($packageFile in $packageFiles) {
    if (-not (Test-Path -LiteralPath $packageFile.FullName)) { continue }
    try { $pkg = Get-Content -LiteralPath $packageFile.FullName -Raw | ConvertFrom-Json } catch { continue }
    if (-not $pkg.PSObject.Properties['name']) { continue }
    $pkgRoot = $packageFile.DirectoryName
    # pnpm bundles these optional native packages inside its own distribution;
    # they are not filtered by the outer install's supportedArchitectures.
    if ($pkg.name -like '@reflink/reflink-*' -and $pkg.name -ne '@reflink/reflink-win32-x64-msvc') {
        Remove-PlatformDirectory $pkgRoot
        continue
    }
    if ($pkg.name -eq 'node-pty') {
        $prebuildRoot = Join-Path $pkgRoot 'prebuilds'
        if (Test-Path -LiteralPath $prebuildRoot) {
            foreach ($dir in Get-ChildItem -LiteralPath $prebuildRoot -Directory) {
                if ($dir.Name -match '^(darwin|linux|win32)-' -and $dir.Name -ne 'win32-x64') { Remove-PlatformDirectory $dir.FullName }
            }
        }
        $conptyRoot = Join-Path $pkgRoot 'third_party\conpty'
        if (Test-Path -LiteralPath $conptyRoot) {
            foreach ($dir in Get-ChildItem -LiteralPath $conptyRoot -Recurse -Directory | Where-Object Name -eq 'win10-arm64') { Remove-PlatformDirectory $dir.FullName }
        }
    }
}
$report = @{target='win32-x64'; removedBytes=$removedBytes; removedPaths=$removedPaths.ToArray()}
[IO.File]::WriteAllText((Join-Path $releaseRoot 'windows-package-report.json'), ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host "Removed non-x64 prebuilds: $([math]::Round($removedBytes/1MB,1)) MiB"
