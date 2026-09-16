# Fail if a fresh Lockwell.exe run does not create settings in an isolated test folder.
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath
)

$ErrorActionPreference = "Stop"
$helper = Join-Path $PSScriptRoot "Invoke-LockwellBootstrapTest.ps1"
& $helper -ExePath $ExePath -Mode Smoke
