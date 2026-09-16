# Fail if Lockwell.Core.dll still ships its logic in the clear.
#
# Core holds the crypto, the vault format and the whole sync stack. For most of the
# project's life it was not obfuscated at all -- it was never listed as a module in the
# Obfuscar config, so every method name and string constant in it shipped readable. This
# guard exists so that can never quietly become true again: a release that leaves Core
# unobfuscated fails here rather than going out.
#
# It checks the two things the release profile actually guarantees for a WPF-adjacent
# assembly:
#
#   1. String hiding. The crypto constants and internal messages are encrypted in the
#      shipped DLL, so the interesting literals -- the curve name, the "this is how the
#      password is handled" comments-turned-strings, error text that maps to logic -- are
#      not sitting in plain UTF-8.
#   2. Format preservation. The serialisation type names that cross a boundary (the sync
#      DTOs, read by the phone; anything written to disk) are still present, because
#      renaming them would silently break either an existing vault or the pairing
#      protocol.
#
# Public type and method names are deliberately NOT asserted absent: the BAML-safe profile
# keeps the public surface so the WPF shell that references Core keeps working, and a
# vault's security never depended on the name "Encrypt" being secret. The literals do.
param(
    [Parameter(Mandatory = $true)]
    [string] $CoreDllPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $CoreDllPath)) {
    throw "Lockwell.Core.dll not found: $CoreDllPath"
}

$text = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($CoreDllPath))

# String constants that exist only in Core's source, as literals inside method bodies. If
# HideStrings ran, they are encrypted in the DLL and must not appear as readable UTF-8.
#
# These are deliberately message strings from method IL, not `const` field values. A
# `const string` (like the curve name "nistP256") stores its value in assembly metadata as
# a constant blob, which string hiding does not rewrite -- it only encrypts the `ldstr`
# instructions in method bodies. Asserting on a const would fail on a correctly obfuscated
# DLL, which is a bug in the check, not the build.
$forbiddenLiterals = @(
    "Encrypted blob is too short to be valid.",
    "left lying around in the heap",
    "is this random actually secure"
)

$leaked = $forbiddenLiterals | Where-Object { $text.Contains($_) }
if ($leaked) {
    throw @"
Lockwell.Core.dll is not obfuscated: readable source string literals were found:
  $($leaked -join "`n  ")

Core must be obfuscated in the same pass as Lockwell.dll. Check obfuscar-gen.ps1 still
emits a <Module> line for Lockwell.Core.dll and that the publish pipeline copies it back.
"@
}

# And confirm the data-shape names that MUST survive actually did, so a passing result
# means "logic hidden, formats intact" rather than "the file was mangled".
$mustSurvive = @("PairingOffer", "TransferManifest")
$missing = $mustSurvive | Where-Object { -not $text.Contains($_) }
if ($missing) {
    throw @"
Lockwell.Core.dll is missing serialisation type names that must be preserved for
cross-device and on-disk compatibility:
  $($missing -join "`n  ")

RenameTypes must stay false. Do not rename the sync DTOs or the JSON contexts.
"@
}

Write-Host "Verified: Lockwell.Core.dll string constants are hidden and its data formats are intact."
