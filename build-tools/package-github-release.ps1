# GitHub release package for slashneck/Lockwell
#
# Produces TWO outputs (source code is never touched):
#   RAW (keep locally):  dist\Lockwell-v{version}-raw\  +  dist\Lockwell-win-x64-raw.zip
#   SHIP (GitHub):       Documents\Lockwell-win-x64.zip  (Standard obfuscation, self-contained folder)
#
# Usage:
#   powershell -File build-tools\package-github-release.ps1
param(
    [string] $Version = "",
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string] $RawFolder = "",
    [string] $RawZipPath = "",
    [string] $GitHubZipPath = (Join-Path $env:TEMP "Lockwell-win-x64.zip")
)

$ErrorActionPreference = "Stop"

$csproj = Join-Path $RepoRoot "Lockwell\Lockwell.csproj"
if (-not $Version) {
    [xml] $proj = Get-Content $csproj
    $Version = $proj.Project.PropertyGroup.Version | Select-Object -First 1
    if (-not $Version) { throw "Could not read Version from Lockwell.csproj" }
}

$distDir = Join-Path $RepoRoot "dist"
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

if (-not $RawFolder) {
    $RawFolder = Join-Path $distDir "Lockwell-v$Version-raw"
}
if (-not $RawZipPath) {
    $RawZipPath = Join-Path $distDir "Lockwell-win-x64-raw.zip"
}

$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$obfStaging = Join-Path $distDir "github-release-staging"

Write-Host "=== Step 1/3: Raw consumer build (local only, no obfuscation) ==="
& $publishScript -OutputPath $RawFolder -Obfuscate:$false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $RawZipPath) { Remove-Item $RawZipPath -Force }
Write-Host "Zipping raw folder -> $RawZipPath"
Compress-Archive -LiteralPath $RawFolder -DestinationPath $RawZipPath -CompressionLevel Optimal -Force
$rawMb = [math]::Round((Get-Item $RawZipPath).Length / 1MB, 1)
Write-Host "  Raw zip: $RawZipPath ($rawMb MB)"

Write-Host ""
Write-Host "=== Step 2/3: Standard obfuscation build for GitHub (WPF-safe) ==="
if (Test-Path $obfStaging) { Remove-Item $obfStaging -Recurse -Force }
& $publishScript -OutputPath $obfStaging -Obfuscate -ObfuscationProfile Standard
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "=== Step 3/3: GitHub zip -> Documents ==="
if (Test-Path $GitHubZipPath) { Remove-Item $GitHubZipPath -Force }
$shipItems = Get-ChildItem -LiteralPath $obfStaging -Force
if (-not $shipItems) { throw "Obfuscated staging folder is empty: $obfStaging" }
Compress-Archive -Path ($shipItems | ForEach-Object { $_.FullName }) -DestinationPath $GitHubZipPath -CompressionLevel Optimal -Force
Remove-Item $obfStaging -Recurse -Force

$rawExe = Join-Path $RawFolder "Lockwell.exe"
if ((Test-Path $rawExe) -and (Test-Path $GitHubZipPath)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $rawDll = Join-Path $RawFolder "Lockwell.dll"
    $tmpShip = Join-Path $env:TEMP ("lw-ship-" + [Guid]::NewGuid().ToString("n") + ".dll")
    $zip = [IO.Compression.ZipFile]::OpenRead($GitHubZipPath)
    $entry = $zip.Entries | Where-Object { $_.Name -eq "Lockwell.dll" } | Select-Object -First 1
    if (-not $entry) {
        $zip.Dispose()
        throw "Ship zip does not contain Lockwell.dll."
    }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tmpShip, $true)
    $zip.Dispose()
    if (-not (Test-Path $rawDll)) {
        throw "Raw folder missing Lockwell.dll: $rawDll"
    }
    $rawHash = (Get-FileHash $rawDll).Hash
    $shipHash = (Get-FileHash $tmpShip).Hash
    Remove-Item $tmpShip -Force -ErrorAction SilentlyContinue
    if ($rawHash -eq $shipHash) {
        throw "Shipped Lockwell.dll is byte-identical to raw. Obfuscation did not reach the GitHub zip."
    }
    Write-Host "Verified: shipped Lockwell.dll differs from raw build."
}

$shipMb = [math]::Round((Get-Item $GitHubZipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "Done."
Write-Host "  KEEP (raw folder):     $RawFolder"
Write-Host "  KEEP (raw zip):        $RawZipPath"
Write-Host "  UPLOAD TO GITHUB:      $GitHubZipPath ($shipMb MB)"
Write-Host ""
Write-Host "GitHub asset name must be: Lockwell-win-x64.zip"
Write-Host ""
Write-Host "  gh release create v$Version `"Lockwell v$Version`" `"$GitHubZipPath`" --repo slashneck/Lockwell"
