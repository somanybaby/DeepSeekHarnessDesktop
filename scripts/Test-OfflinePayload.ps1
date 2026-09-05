[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BootstrapperAssembly,
    [Parameter(Mandatory)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$verifyRoot = [IO.Path]::GetFullPath($OutputRoot)
if ((Test-Path -LiteralPath $verifyRoot) -and (Get-ChildItem -LiteralPath $verifyRoot -Force | Select-Object -First 1)) {
    throw 'Verification output must be empty; no existing files will be overwritten.'
}
# Invoke the exact extractor compiled into the setup; no installation, GUI,
# shortcuts or changes to the existing user profile are performed here.
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($BootstrapperAssembly))
$program = $assembly.GetType('DeepSeekHarnessDesktopSetup.Program', $true)
$flags = [Reflection.BindingFlags]'NonPublic, Static'
try {
    $program.GetMethod('ExtractPayload', $flags).Invoke($null, @($verifyRoot))
    $payload = Join-Path $verifyRoot 'payload'
    $validationArguments = [object[]]@(
        [string](Join-Path $payload 'app'), [string](Join-Path $payload 'runtime'), [string](Join-Path $payload 'webview2'))
    $program.GetMethod('ValidatePayload', $flags).Invoke($null, $validationArguments)
} catch {
    if ($_.Exception.InnerException) { throw $_.Exception.InnerException }
    throw
}
& (Join-Path $PSScriptRoot 'Test-WindowsRuntimeSeed.ps1') -RuntimeSeed (Join-Path $payload 'runtime')
Write-Host "PASS Real installer extraction and payload validation: $verifyRoot"
