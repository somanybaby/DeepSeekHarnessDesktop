[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$NodeSource,
    [Parameter(Mandatory)][string]$PnpmScript,
    [Parameter(Mandatory)][string]$StoreDirectory,
    [string]$HarnessVersion = '0.1.2-rc.1'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Environment]::Is64BitProcess -or $env:OS -ne 'Windows_NT') { throw 'Build on Windows x64.' }
if ($HarnessVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]{0,127}$') { throw 'Invalid version.' }
$seedRoot = [IO.Path]::GetFullPath($OutputRoot)
if ((Test-Path -LiteralPath $seedRoot) -and (Get-ChildItem -LiteralPath $seedRoot -Force | Select-Object -First 1)) {
    throw 'OutputRoot must be empty. Existing runtimes are never modified.'
}
$storeRoot = [IO.Path]::GetFullPath($StoreDirectory)
$nodeSourceRoot = [IO.Path]::GetFullPath($NodeSource)
if (-not (Test-Path -LiteralPath (Join-Path $nodeSourceRoot 'node.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $nodeSourceRoot 'node_modules\npm\bin\npm-cli.js'))) {
    throw 'NodeSource must be a private Windows Node distribution including npm.'
}
if ($storeRoot.StartsWith($seedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $storeRoot -eq $seedRoot) {
    throw 'The package cache must be outside the distributable seed.'
}
$releaseRoot = Join-Path $seedRoot "releases\$HarnessVersion"
if ($releaseRoot.Length -gt 120) { throw 'Use a shorter OutputRoot for Windows install scripts.' }
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$nodeDestination = Join-Path $seedRoot 'node'
& robocopy $nodeSourceRoot $nodeDestination /E /COPY:DAT /DCOPY:DAT /R:2 /W:1 /XJ /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw 'Node.js copy failed.' }
$nodeExe = Join-Path $nodeDestination 'node.exe'
$pnpmVersion = & $nodeExe $PnpmScript --version
if ($LASTEXITCODE -ne 0 -or $pnpmVersion.Trim() -ne '11.7.0') { throw 'The build requires pnpm 11.7.0.' }
$metadata = Invoke-RestMethod -Uri "https://registry.npmjs.org/%40deepseek-ai%2Fdsh/$HarnessVersion"
$archive = Join-Path $releaseRoot 'official-package.tgz'
$tarballUri = [uri]$metadata.dist.tarball
if ($tarballUri.Scheme -ne 'https' -or $tarballUri.Host -ne 'registry.npmjs.org') { throw 'Unexpected registry tarball host.' }
Invoke-WebRequest -Uri $tarballUri -OutFile $archive
$expected = @(([string]$metadata.dist.integrity).Split(' ') | Where-Object { $_.StartsWith('sha512-') })[0]
$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA512
$actual = 'sha512-' + [Convert]::ToBase64String([Convert]::FromHexString($hash.Hash))
if ($actual -cne $expected) { throw 'Official Harness archive failed SHA-512 verification.' }
[IO.File]::WriteAllText("$archive.sha512", $actual, [Text.Encoding]::ASCII)
$package = @{ name='deepseek-harness-desktop-runtime'; version='1.0.0'; private=$true; dependencies=@{
    '@deepseek-ai/dsh'='file:./official-package.tgz'; pnpm='11.7.0'
} } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $releaseRoot 'package.json'), $package, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\packaging\windows-pnpm-workspace.yaml') -Destination (Join-Path $releaseRoot 'pnpm-workspace.yaml')
$savedPath = $env:PATH
try {
    $env:PATH = "$nodeDestination;$savedPath"
    & $nodeExe $PnpmScript install --prod --no-frozen-lockfile --reporter=append-only --registry=https://registry.npmjs.org/ --store-dir $storeRoot --dir $releaseRoot
    if ($LASTEXITCODE -ne 0) { throw 'Windows runtime installation failed.' }
} finally { $env:PATH = $savedPath }
& (Join-Path $PSScriptRoot 'Optimize-WindowsRuntime.ps1') -ReleaseDirectory $releaseRoot
$cliEntry = Join-Path $releaseRoot 'node_modules\@deepseek-ai\dsh\lib\bin.js'
$version = & $nodeExe $cliEntry --version
if ($LASTEXITCODE -ne 0 -or $version.Trim() -ne $HarnessVersion) { throw 'Harness version smoke test failed.' }
$links = Get-ChildItem -LiteralPath $releaseRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1
if ($links) { throw "Non-portable link found: $($links.FullName)" }
$manifest = @{schemaVersion=1; version=$HarnessVersion; releaseDirectory="releases/$HarnessVersion";
    entryPoint='node_modules/@deepseek-ai/dsh/lib/bin.js'; toolsDirectory='node_modules/.bin'; activatedAt=[DateTimeOffset]::UtcNow.ToString('O')}
[IO.File]::WriteAllText((Join-Path $seedRoot 'current.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Test-WindowsRuntimeSeed.ps1') -RuntimeSeed $seedRoot
Write-Host "Windows x64 seed ready: $seedRoot"
