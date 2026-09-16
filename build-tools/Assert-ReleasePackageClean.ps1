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
    $patterns = @()
    foreach ($needle in $needles) {
        $ascii = [System.Text.Encoding]::ASCII.GetBytes($needle.ToLowerInvariant())
        $utf16 = [System.Text.Encoding]::Unicode.GetBytes($needle.ToLowerInvariant())
        $patterns += , @{ Name = $needle; Bytes = $ascii }
        $patterns += , @{ Name = $needle; Bytes = $utf16 }
    }

    function Test-BytesContain {
        param([byte[]] $Haystack, [byte[]] $Needle)

        $limit = $Haystack.Length - $Needle.Length
        if ($limit -lt 0) { return $false }

        for ($i = 0; $i -le $limit; $i++) {
            $match = $true
            for ($j = 0; $j -lt $Needle.Length; $j++) {
                $b = $Haystack[$i + $j]
                # Fold ASCII upper-case to lower so a path is caught whatever case it
                # was written in. UTF-16 bytes fold the same way; the interleaved zero
                # bytes are unaffected.
                if ($b -ge 65 -and $b -le 90) { $b = $b + 32 }
                if ($b -ne $Needle[$j]) { $match = $false; break }
            }
            if ($match) { return $true }
        }
        return $false
    }

    $leaks = @()
    foreach ($file in Get-ChildItem -LiteralPath $RootPath -Recurse -File -ErrorAction SilentlyContinue) {
        $relative = $file.FullName.Substring($RootPath.Length).TrimStart('\')

        foreach ($needle in $needles) {
            if ($relative -like "*$needle*") {
                $leaks += "$relative (in the file name: $needle)"
            }
        }

        # The .NET runtime files are large, numerous, and not ours. Scanning them adds
        # minutes and finds nothing, because nothing on this machine built them.
        if ($file.Extension -notin @(".exe", ".dll", ".json", ".xml", ".txt", ".md", ".config", ".pdb")) { continue }
        if ($file.Name -like "System.*" -or $file.Name -like "Microsoft.*" -or $file.Name -like "WindowsBase*") { continue }
        if ($file.Length -gt 80MB) { continue }

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        foreach ($pattern in $patterns) {
            if (Test-BytesContain -Haystack $bytes -Needle $pattern.Bytes) {
                $leaks += "$relative (contains: $($pattern.Name))"
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
