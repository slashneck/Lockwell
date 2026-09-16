# Builds the Lockwell package that gets attached to a GitHub release.
#
# One artifact, not two. Earlier versions of this script produced a raw build to keep
# locally and an obfuscated one to publish, and compared them to prove obfuscation had
# actually reached the shipped DLL. Public releases are no longer obfuscated, so there is
# nothing to compare: what is published is what this repository builds.
#
# That is deliberate. Obfuscation protected nothing once the source was published, and it
# made the one thing open source is good for impossible, which is checking that the
# binary on the releases page came from the code on the repository page. Vault security
# is unaffected either way: it rests on Argon2id and AES-256-GCM keyed from the user's
# master password, and there is no secret in the binary for obfuscation to hide.
#
# Usage:
#   powershell -File build-tools\package-github-release.ps1
param(
    [string] $Version = "",
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string] $OutputZipPath = (Join-Path $env:TEMP "Lockwell-win-x64.zip"),
    # Off for public releases. See publish-release.ps1 for why.
    [switch] $Obfuscate
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

$staging = Join-Path $distDir "github-release-staging"
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"

Write-Host "=== Building Lockwell $Version ==="
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

& $publishScript -OutputPath $staging -Obfuscate:$Obfuscate
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "=== Packaging ==="
if (Test-Path $OutputZipPath) { Remove-Item $OutputZipPath -Force }

$shipItems = Get-ChildItem -LiteralPath $staging -Force
if (-not $shipItems) { throw "Staging folder is empty: $staging" }

# The exe sits at the root of the zip. Orbit extracts the archive as-is and expects to
# find it there or one folder down, never deeper.
Compress-Archive -Path ($shipItems | ForEach-Object { $_.FullName }) `
    -DestinationPath $OutputZipPath -CompressionLevel Optimal -Force
Remove-Item $staging -Recurse -Force

$zip = Get-Item $OutputZipPath
$sizeMb = [math]::Round($zip.Length / 1MB, 1)
$sha = (Get-FileHash $OutputZipPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Done."
Write-Host "  Package:  $OutputZipPath"
Write-Host "  Size:     $($zip.Length) bytes ($sizeMb MB)"
Write-Host "  SHA-256:  $sha"
Write-Host ""
Write-Host "The GitHub asset name must be Lockwell-win-x64.zip, because that is the name"
Write-Host "the signed release manifest refers to."
Write-Host ""
Write-Host "Next: sign it, then publish."
Write-Host "  ReleaseKit sign --private <offline key> --package `"$OutputZipPath`" --version $Version --out <dir>"
Write-Host "  gh release create v$Version --repo slashneck/Lockwell <package> <release.json> <release.json.sig>"
