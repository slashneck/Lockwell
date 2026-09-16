# Publish LockwellSetup.exe, the standalone installer attached to a GitHub release.
#
# Not obfuscated. The installer is the component that decides whether a downloaded
# release is genuine before writing it into Program Files, so of everything Lockwell
# ships it is the piece most worth being able to read. Obfuscating it hid that decision
# from the people relying on it and protected nothing: the check it performs is a
# signature verification against a public key, and knowing exactly how it works is what
# makes it trustworthy rather than what breaks it.
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string] $OutputPath = (Join-Path $env:TEMP "LockwellSetup.exe"),
    [switch] $Obfuscate
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $RepoRoot "Lockwell.Installer\Lockwell.Installer.csproj"
$projectDir = Join-Path $RepoRoot "Lockwell.Installer"
$obfuscateScript = Join-Path $PSScriptRoot "Invoke-OutputObfuscation.ps1"

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

$staging = Join-Path $RepoRoot "dist\installer-staging"

Write-Host "Publishing installer..."
$built = Publish-InstallerExe -Staging $staging -Obfuscate:$Obfuscate.IsPresent
Copy-InstallerOutput -SourceExe ([string]$built) -DestPath $OutputPath
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$exe = Get-Item $OutputPath
$sizeMb = [math]::Round($exe.Length / 1MB, 1)
$sha = (Get-FileHash $OutputPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Done."
Write-Host "  Installer: $OutputPath ($sizeMb MB)"
Write-Host "  SHA-256:   $sha"
Write-Host ""
Write-Host "Attach it to the release as LockwellSetup.exe. It downloads"
Write-Host "Lockwell-win-x64.zip from the same release and verifies the signature"
Write-Host "before writing anything."
