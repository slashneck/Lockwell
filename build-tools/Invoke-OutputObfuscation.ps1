# Obfuscate every Release build copy of an assembly under bin/ and obj/ (required before single-file publish).
param(
    [Parameter(Mandatory = $true)]
    [string] $ProjectDir,
    [Parameter(Mandatory = $true)]
    [string] $AssemblyName,
    [ValidateSet("Standard", "Max")]
    [string] $Profile = "Max",
    [string] $GenScript = (Join-Path $PSScriptRoot "obfuscar-gen.ps1"),
    [string] $InstallerGenScript = (Join-Path $PSScriptRoot "obfuscar-installer-gen.ps1")
)

$ErrorActionPreference = "Stop"

$projectDir = (Resolve-Path $ProjectDir).Path
$dllName = "$AssemblyName.dll"

$csproj = Get-ChildItem $projectDir -Filter *.csproj -File | Select-Object -First 1
if (-not $csproj) {
    throw "No .csproj found in $projectDir"
}

$obfuscar = & dotnet msbuild $csproj.FullName -getProperty:Obfuscar 2>$null
if (-not $obfuscar) {
    $fallback = Join-Path $env:USERPROFILE ".nuget\packages\obfuscar\2.2.49\tools\Obfuscar.Console.exe"
    if (Test-Path $fallback) { $obfuscar = $fallback }
}
if (-not $obfuscar -or -not (Test-Path $obfuscar)) {
    throw "Obfuscar executable not found. Restore the project NuGet packages first."
}

$isInstaller = $AssemblyName -eq "LockwellSetup"
$gen = if ($isInstaller) { $InstallerGenScript } else { $GenScript }

$roots = @(
    (Join-Path $projectDir "bin"),
    (Join-Path $projectDir "obj")
)

$dlls = foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem $root -Recurse -Filter $dllName -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -match '\\Release\\' -and
            $_.FullName -notmatch '\\ref\\' -and
            $_.FullName -notmatch '\\refint\\'
        }
}

$dlls = $dlls | Sort-Object FullName -Unique
if (-not $dlls) {
    throw "No Release $dllName found under $projectDir\bin or obj. Build Release first."
}

Write-Host "Obfuscating $($dlls.Count) $dllName cop(ies) with profile: $Profile"

foreach ($dll in $dlls) {
    $dir = $dll.Directory.FullName
    $out = Join-Path $dir "obf_out"
    $cfg = Join-Path $dir "obfuscar.inline.gen.xml"

    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    if (Test-Path $cfg) { Remove-Item $cfg -Force }

    if ($isInstaller) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $gen -InPath $dir -OutPath $out -OutConfig $cfg
    } else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $gen -InPath $dir -OutPath $out -OutConfig $cfg -Profile $Profile
    }

    if ($LASTEXITCODE -ne 0) { throw "obfuscar-gen failed for $dir" }

    & $obfuscar $cfg 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed for $dir" }

    $obfDll = Join-Path $out $dllName
    if (-not (Test-Path $obfDll)) {
        throw "Expected obfuscated output: $obfDll"
    }

    Copy-Item $obfDll $dll.FullName -Force
    Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $cfg -Force -ErrorAction SilentlyContinue
    Write-Host "  OK: $($dll.FullName)"
}

Write-Host "Obfuscation complete for $AssemblyName."
