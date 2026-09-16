# Launch Lockwell normally and verify first-run settings bootstrap (catches WPF/BAML breakage).
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath,
    [int] $WaitSeconds = 10
)

$ErrorActionPreference = "Stop"
$helper = Join-Path $PSScriptRoot "Invoke-LockwellBootstrapTest.ps1"
& $helper -ExePath $ExePath -Mode Ui -UiWaitSeconds $WaitSeconds
