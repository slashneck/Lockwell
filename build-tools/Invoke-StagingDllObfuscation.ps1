# Obfuscate the main app DLL inside a publish folder (in place).
param(
    [Parameter(Mandatory = $true)]
    [string] $StagingFolder,
    [Parameter(Mandatory = $true)]
    [string] $AssemblyName,
    [ValidateSet("Standard", "Max")]
    [string] $Profile = "Max",
    [string] $GenScript = (Join-Path $PSScriptRoot "obfuscar-gen.ps1")
)

$ErrorActionPreference = "Stop"

$staging = (Resolve-Path $StagingFolder).Path
$dllPath = Join-Path $staging "$AssemblyName.dll"
if (-not (Test-Path $dllPath)) {
    throw "Expected publish output DLL not found: $dllPath"
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $repoRoot "Lockwell\Lockwell.csproj"

$obfuscar = & dotnet msbuild $proj -getProperty:Obfuscar 2>$null
if (-not $obfuscar -or -not (Test-Path $obfuscar)) {
    $fallback = Join-Path $env:USERPROFILE ".nuget\packages\obfuscar\2.2.49\tools\Obfuscar.Console.exe"
    if (Test-Path $fallback) { $obfuscar = $fallback }
}
if (-not $obfuscar -or -not (Test-Path $obfuscar)) {
    throw "Obfuscar executable not found."
}

$out = Join-Path $staging "obf_out"
$cfg = Join-Path $staging "obfuscar.staging.gen.xml"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $cfg) { Remove-Item $cfg -Force }

& powershell -NoProfile -ExecutionPolicy Bypass -File $GenScript `
    -InPath $staging -OutPath $out -OutConfig $cfg -Profile $Profile
if ($LASTEXITCODE -ne 0) { throw "obfuscar-gen failed for $staging" }

& $obfuscar $cfg 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed for $staging" }

# The generator emits a module for the app DLL and, when it is present in the same folder,
# one for Lockwell.Core.dll -- obfuscated together in this single pass so their
# cross-references rename consistently. Copy back every DLL Obfuscar actually produced,
# not just the one named on the command line, or Core would be re-obfuscated in isolation
# on a later pass (or, worse, left in the clear).
$producedAny = $false
foreach ($obf in Get-ChildItem $out -Filter *.dll -File -ErrorAction SilentlyContinue) {
    $target = Join-Path $staging $obf.Name
    if (-not (Test-Path $target)) { continue }
    Copy-Item $obf.FullName $target -Force
    Write-Host "  Obfuscated in publish folder: $target ($Profile)"
    $producedAny = $true
}

if (-not $producedAny) {
    throw "Obfuscar produced no output DLLs in $out"
}

$mainObf = Join-Path $out "$AssemblyName.dll"
if (-not (Test-Path $mainObf)) {
    throw "Obfuscated main DLL missing: $mainObf"
}

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $cfg -Force -ErrorAction SilentlyContinue
Write-Host "Obfuscation complete ($Profile)."
