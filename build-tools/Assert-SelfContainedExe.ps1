# Fail the release if a shipped Lockwell build still requires a machine-wide .NET install.
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath"
}

$dir = Split-Path $ExePath -Parent
$runtimeConfig = Join-Path $dir "Lockwell.runtimeconfig.json"

$includedFromConfig = $false
if (Test-Path -LiteralPath $runtimeConfig) {
    try {
        $json = Get-Content $runtimeConfig -Raw | ConvertFrom-Json
        if ($null -ne $json.runtimeOptions.includedFrameworks -and $json.runtimeOptions.includedFrameworks.Count -gt 0) {
            $includedFromConfig = $true
        }
    } catch {
        # fall through
    }
}

$hasNativeRuntime = @("coreclr.dll", "hostfxr.dll") | Where-Object { Test-Path (Join-Path $dir $_) }

if ($includedFromConfig -or $hasNativeRuntime) {
    Write-Host "Verified: $ExePath is self-contained (no separate .NET install required)."
    return
}

$text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($ExePath))
$isSelfContained = $text.Contains("includedFrameworks")
$isFrameworkDependent = $text.Contains('"frameworks"')

if (-not $isSelfContained -or $isFrameworkDependent) {
    throw @"
$ExePath is framework-dependent (requires .NET 8 Desktop Runtime on the PC).

Consumer releases must be self-contained. Re-run build-tools\publish-release.ps1 with the fixed pipeline.
"@
}

Write-Host "Verified: $ExePath is self-contained (no separate .NET install required)."
