# Prove an obfuscated build can still create, encrypt, and read a vault.
#
# This is the correctness half of obfuscating Core. Renaming methods and hiding strings in
# the crypto and vault code is worthless if it also breaks opening a vault, and a broken
# vault is the one failure this project cannot ship -- a user's data would be unreadable
# by the very build meant to protect it.
#
# --store-shots drives the real application against a throwaway demo vault: it derives a
# key with Argon2, encrypts and decrypts media through AES-GCM, serialises the vault, and
# renders every screen. If all of that survives obfuscation, the PNGs appear. If Core was
# mangled, it throws on the first decrypt and nothing is written. So the presence of the
# captures is the proof. The real vault is never touched: the harness sets its own data
# redirect to a temp folder before anything reads the default location.
param(
    [Parameter(Mandatory = $true)]
    [string] $ExePath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath"
}

$shotDir = Join-Path ([System.IO.Path]::GetTempPath()) ("lockwell-obf-roundtrip-" + [Guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Path $shotDir -Force | Out-Null

try {
    $proc = Start-Process -FilePath $ExePath -ArgumentList @("--store-shots", "`"$shotDir`"") `
        -PassThru -Wait -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) {
        throw "Obfuscated build exited with code $($proc.ExitCode) while exercising the vault."
    }

    $windowDir = Join-Path $shotDir "window"
    $pngs = @()
    if (Test-Path $windowDir) {
        $pngs = Get-ChildItem $windowDir -Filter *.png -File -ErrorAction SilentlyContinue
    }

    if ($pngs.Count -lt 5) {
        throw @"
The obfuscated build did not produce the expected screen captures ($($pngs.Count) found).
That means the demo vault could not be created, decrypted, or rendered through the
obfuscated Core -- obfuscation has broken the vault path. Do not ship this build.
"@
    }

    Write-Host "Verified: obfuscated build created and read a vault ($($pngs.Count) screens rendered)."
}
finally {
    if (Test-Path $shotDir) {
        Remove-Item $shotDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
