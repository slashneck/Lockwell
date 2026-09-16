# Fail if a shipped consumer exe still contains obvious unobfuscated symbols.
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath"
}

$text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($ExePath))
$dllPath = Join-Path (Split-Path $ExePath -Parent) "Lockwell.dll"
if (Test-Path -LiteralPath $dllPath) {
    $text += [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($dllPath))
}

if ($text.Contains("VaultManager") -or $text.Contains("Lockwell.Services.VaultManager")) {
    throw "Obfuscation verification failed: readable VaultManager symbols found in $ExePath"
}

Write-Host "Verified: $ExePath looks obfuscated."
