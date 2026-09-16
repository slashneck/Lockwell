# Fail the release publish if user vault data or dev artifacts appear in the output tree.
param(
    [Parameter(Mandatory = $true)]
    [string] $RootPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $RootPath)) {
    throw "Package root not found: $RootPath"
}

$forbiddenNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    "vault.lwv",
    "settings.json",
    "THREAT-MODEL.md",
    "VAULT-FORMAT.md"
) | ForEach-Object { [void]$forbiddenNames.Add($_) }

$forbiddenDirNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    "attachments"
) | ForEach-Object { [void]$forbiddenDirNames.Add($_) }

$hits = Get-ChildItem -LiteralPath $RootPath -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $forbiddenNames.Contains($_.Name) }

if ($hits) {
    $list = ($hits | ForEach-Object { $_.FullName.Substring($RootPath.Length).TrimStart('\') }) -join "`n  "
    throw @"
Release package contains user or dev-only files (must NOT ship):
  $list

Publish from build-tools scripts only. Never copy %LocalAppData%\Lockwell into the package.
"@
}

$dirHits = Get-ChildItem -LiteralPath $RootPath -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $forbiddenDirNames.Contains($_.Name) }

if ($dirHits) {
    $list = ($dirHits | ForEach-Object { $_.FullName.Substring($RootPath.Length).TrimStart('\') }) -join "`n  "
    throw @"
Release package contains user data folders (must NOT ship):
  $list
"@
}

Write-Host "Verified: no user vault data or dev-only docs in package."

# ---------------------------------------------------------------------------
# Nothing in a public package may identify the machine it was built on.
#
# The obvious leak is a file someone copied in by hand, and the checks above catch
# that. The one that actually happened is subtler: build tooling embedding an
# absolute source path, so "C:\Users\<name>\..." ends up inside a compiled assembly
# where no directory listing will ever show it. So this scans file contents too, as
# bytes, in both ASCII and UTF-16 -- .NET metadata stores strings as UTF-16, and a
# plain text search silently misses every one of them.
#
# The needles come from the environment rather than being written down here. A guard
# against leaking a username should not itself contain one.
# ---------------------------------------------------------------------------

$needles = @($env:USERNAME, $env:COMPUTERNAME, $env:USERDOMAIN) |
    Where-Object { $_ -and $_.Length -ge 4 } |
    Select-Object -Unique

if (-not $needles) {
    Write-Host "Skipped identity scan: no usable machine identifiers in the environment."
} else {
    # Latin1 maps every byte to the code point of the same value and back, so a byte
    # array round-trips through a string without loss. That lets the search itself run
    # as a native IndexOf over the whole file instead of a PowerShell loop over
    # individual bytes, which on a folder of self-contained runtime DLLs is the
    # difference between seconds and not finishing.
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)

    $asciiNeedles = @{}
    $utf16Needles = @{}
    foreach ($needle in $needles) {
        $lower = $needle.ToLowerInvariant()
        $asciiNeedles[$needle] = $lower
        # UTF-16LE is how .NET metadata stores strings: each character followed by a
        # zero byte, which a plain text search would never match.
        $utf16Needles[$needle] = $latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($lower))
    }

    $leaks = @()
    foreach ($file in Get-ChildItem -LiteralPath $RootPath -Recurse -File -ErrorAction SilentlyContinue) {
        $relative = $file.FullName.Substring($RootPath.Length).TrimStart([char]92)

        foreach ($needle in $needles) {
            if ($relative -like "*$needle*") {
                $leaks += "$relative (in the file name: $needle)"
            }
        }

        # The .NET runtime files are numerous and not ours: nothing on this machine
        # built them, so nothing from this machine can be inside them.
        if ($file.Extension -notin @(".exe", ".dll", ".json", ".xml", ".txt", ".md", ".config", ".pdb")) { continue }
        if ($file.Name -like "System.*" -or $file.Name -like "Microsoft.*" -or $file.Name -like "WindowsBase*") { continue }
        if ($file.Length -gt 80MB) { continue }

        $text = $latin1.GetString([System.IO.File]::ReadAllBytes($file.FullName)).ToLowerInvariant()

        foreach ($needle in $needles) {
            if ($text.Contains($asciiNeedles[$needle]) -or $text.Contains($utf16Needles[$needle])) {
                $leaks += "$relative (contains: $needle)"
                break
            }
        }
    }

    if ($leaks) {
        $list = ($leaks | Select-Object -Unique) -join "`n  "
        throw @"
Release package leaks identifying information about the build machine:
  $list

This must be fixed before publishing. A user name or machine name embedded in a
shipped file is a privacy leak that no user can remove.
"@
    }

    Write-Host "Verified: no build-machine identifiers in package ($($needles.Count) checked)."
}
