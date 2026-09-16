# Publish Lockwell to a self-contained folder (raw or obfuscated). Source code is never modified.
# WPF is shipped as a folder publish: single-file bundling breaks BAML at startup.
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    # Public releases are NOT obfuscated, and this switch is off by default.
    #
    # Lockwell used to ship obfuscated because the source was private, so making the
    # binary hard to read was part of the story. With the source published under the GPL
    # that reasoning is gone: obfuscation now hides an algorithm anyone can read in the
    # repository, and it costs something real. A published build that nobody can
    # reproduce from the published source cannot be checked against it, and for a vault
    # app being checkable is the point.
    #
    # It changes nothing about vault security either way. The vault is protected by
    # Argon2id and AES-256-GCM keyed from the user's master password. There is no secret
    # compiled into the binary for obfuscation to protect: the only key in the source is
    # the release signing PUBLIC key, which is public by design.
    #
    # The switch stays because the tooling still works and someone building privately may
    # want it.
    [switch] $Obfuscate,
    [ValidateSet("Standard", "Max")]
    # Standard, not Max. Max renames the public API, which breaks the WPF shell's BAML at
    # startup (Baml2006SchemaContext.ResolveBamlType throws).
    [string] $ObfuscationProfile = "Standard"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$stagingObfuscateScript = Join-Path $PSScriptRoot "Invoke-StagingDllObfuscation.ps1"
$outputObfuscateScript = Join-Path $PSScriptRoot "Invoke-OutputObfuscation.ps1"
$assertSelfContained = Join-Path $PSScriptRoot "Assert-SelfContainedExe.ps1"
$assertObfuscated = Join-Path $PSScriptRoot "Assert-ObfuscatedExe.ps1"
$assertSettingsBootstrap = Join-Path $PSScriptRoot "Assert-SettingsBootstrap.ps1"
$assertUiBootstrap = Join-Path $PSScriptRoot "Assert-UiBootstrap.ps1"
$projectDir = Join-Path $repoRoot "Lockwell"

# Target the desktop project explicitly. This used to be an undefined variable, so both the
# clean and the publish below fell back to the solution in the working directory -- which
# now includes the Android project, and that cannot restore for win-x64, so the very first
# step failed. Publishing the app means publishing the app, not the whole solution.
$proj = Join-Path $projectDir "Lockwell.csproj"

$folderStaging = Join-Path ([System.IO.Path]::GetTempPath()) ("lockwell-publish-folder-" + [Guid]::NewGuid().ToString("n"))
$deleteFolderStaging = $true

function Publish-FolderSelfContained {
    param([string] $Destination)

    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    & dotnet publish $proj -c Release -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:LockwellReleasePublish=false `
        -p:DebugType=portable `
        -p:DebugSymbols=true `
        -o $Destination 2>&1 | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish (folder) failed (exit $LASTEXITCODE)"
    }
}

try {
    # Restore for the runtime identifier we are about to clean and publish for. Without
    # this, a working tree whose last restore was a plain Debug build fails at the very
    # first step with NETSDK1047: "clean" reads the assets file too, and the assets file
    # has no win-x64 target in it yet.
    Write-Host "dotnet restore (win-x64)..."
    & dotnet restore $proj -r win-x64 --verbosity minimal 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "dotnet clean (full rebuild before publish)..."
    & dotnet clean $proj -c Release -r win-x64 --verbosity minimal 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $binRelease = Join-Path $projectDir "bin\Release"
    $objRelease = Join-Path $projectDir "obj\Release"
    if (Test-Path $binRelease) { Remove-Item $binRelease -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $objRelease) { Remove-Item $objRelease -Recurse -Force -ErrorAction SilentlyContinue }

    if ($Obfuscate) {
        Write-Host "Step 1/2: Self-contained folder publish..."
        Publish-FolderSelfContained -Destination $folderStaging
        $deleteFolderStaging = $false

        Write-Host "Step 2/2: Obfuscate publish folder DLL ($ObfuscationProfile)..."
        & $stagingObfuscateScript -StagingFolder $folderStaging -AssemblyName Lockwell -Profile $ObfuscationProfile
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } else {
        Write-Host "Publishing raw self-contained folder (no obfuscation)..."
        Publish-FolderSelfContained -Destination $folderStaging
        $deleteFolderStaging = $false
    }

    $publishedExe = Join-Path $folderStaging "Lockwell.exe"
    if (-not (Test-Path $publishedExe)) {
        throw "Missing Lockwell.exe in publish output."
    }

    & $assertSelfContained -ExePath $publishedExe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Obfuscate) {
        if ($ObfuscationProfile -eq "Max") {
            & $assertObfuscated -ExePath $publishedExe
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }

        # Core carries the crypto and the vault format. Prove it is obfuscated.
        $assertCoreObfuscated = Join-Path $PSScriptRoot "Assert-CoreObfuscated.ps1"
        & $assertCoreObfuscated -CoreDllPath (Join-Path $folderStaging "Lockwell.Core.dll")
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    # This one runs for every build, obfuscated or not. It creates a vault, encrypts into
    # it, and reads it back through the exe that is about to be shipped. It was written to
    # catch obfuscation breaking the vault format, but the thing it actually guards is
    # "the build that ships can open a vault", and that has to be true of every build.
    $assertVaultRoundTrip = Join-Path $PSScriptRoot "Assert-VaultRoundTripObfuscated.ps1"
    & $assertVaultRoundTrip -ExePath $publishedExe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $assertSettingsBootstrap -ExePath $publishedExe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $assertUiBootstrap -ExePath $publishedExe
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if (Test-Path -LiteralPath $OutputPath) {
        Write-Host "Replacing: $OutputPath"
        Remove-Item -LiteralPath $OutputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    Copy-Item (Join-Path $folderStaging "*") $OutputPath -Recurse -Force

    Get-ChildItem $OutputPath -Filter *.xml -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem $OutputPath -Filter *.pdb -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

    $assert = Join-Path $PSScriptRoot "Assert-ReleasePackageClean.ps1"
    & $assert -RootPath $OutputPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    if ($deleteFolderStaging -and (Test-Path -LiteralPath $folderStaging)) {
        Remove-Item -LiteralPath $folderStaging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Published: $OutputPath"
