# Publish Lockwell Setup: raw copy (keep locally) + obfuscated copy (share).
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string] $RawOutputPath = "",
    [string] $ObfuscatedOutputPath = (Join-Path $env:TEMP "LockwellSetup.exe")
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $RepoRoot "Lockwell.Installer\Lockwell.Installer.csproj"
$projectDir = Join-Path $RepoRoot "Lockwell.Installer"
$obfuscateScript = Join-Path $PSScriptRoot "Invoke-OutputObfuscation.ps1"

if (-not $RawOutputPath) {
    $RawOutputPath = Join-Path $RepoRoot "dist\LockwellSetup-raw.exe"
}

function Publish-InstallerExe {
    param(
        [string] $Staging,
        [bool] $Obfuscate
    )

    if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
    New-Item -ItemType Directory -Path $Staging -Force | Out-Null

    $publishCommon = @(
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $Staging
    )

    if ($Obfuscate) {
        Write-Host "Building installer for obfuscated single-file publish..."
        & dotnet build $proj -c Release -r win-x64 -p:LockwellInstallerPublish=true `
            -p:DebugType=None -p:DebugSymbols=false 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

        & $obfuscateScript -ProjectDir $projectDir -AssemblyName LockwellSetup -Profile Max 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Installer obfuscation failed" }

        Write-Host "Publishing obfuscated installer (--no-build)..."
        & dotnet publish $proj --no-build @publishCommon 2>&1 | Out-Host
    } else {
        & dotnet publish $proj @publishCommon -p:LockwellInstallerPublish=false 2>&1 | Out-Host
    }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed (exit $LASTEXITCODE)"
    }

    $built = Join-Path $Staging "LockwellSetup.exe"
    if (-not (Test-Path -LiteralPath $built)) {
        throw "Missing LockwellSetup.exe in $Staging"
    }

    if ($Obfuscate) {
        Write-Host "Verified: obfuscated single-file LockwellSetup.exe published."
    }

    return $built
}

function Copy-InstallerOutput {
    param(
        [string] $SourceExe,
        [string] $DestPath
    )

    $destDir = Split-Path $DestPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Copy-Item $SourceExe $DestPath -Force
}

$rawStaging = Join-Path $RepoRoot "dist\installer-staging-raw"
$obfStaging = Join-Path $RepoRoot "dist\installer-staging-obf"

Write-Host "Publishing raw installer (keep locally)..."
$rawExe = Publish-InstallerExe -Staging $rawStaging -Obfuscate:$false
Copy-InstallerOutput -SourceExe ([string]$rawExe) -DestPath $RawOutputPath
$rawMb = [math]::Round((Get-Item $RawOutputPath).Length / 1MB, 1)
Write-Host "  Raw: $RawOutputPath ($rawMb MB)"

Write-Host ""
Write-Host "Publishing obfuscated installer (share)..."
& dotnet clean $proj -c Release --verbosity minimal | Out-Null
$obfExe = Publish-InstallerExe -Staging $obfStaging -Obfuscate:$true
Copy-InstallerOutput -SourceExe ([string]$obfExe) -DestPath $ObfuscatedOutputPath
$obfMb = [math]::Round((Get-Item $ObfuscatedOutputPath).Length / 1MB, 1)
Write-Host "  Obfuscated: $ObfuscatedOutputPath ($obfMb MB)"

$rawHash = (Get-FileHash $RawOutputPath).Hash
$obfHash = (Get-FileHash $ObfuscatedOutputPath).Hash
if ($rawHash -eq $obfHash) {
    throw "Obfuscated installer is byte-identical to raw. Obfuscation did not reach the shipped LockwellSetup.exe."
}
Write-Host "Verified: obfuscated installer differs from raw build."

Remove-Item $rawStaging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $obfStaging -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done."
Write-Host "  Keep (raw):         $RawOutputPath"
Write-Host "  Share (obfuscated): $ObfuscatedOutputPath"
Write-Host "Recipients only need the obfuscated exe. It downloads Lockwell-win-x64.zip from GitHub releases."
