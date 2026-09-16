# Writes obfuscar.gen.xml for consumer Lockwell publish output.
param(
    [Parameter(Mandatory = $true)]
    [string] $InPath,
    [Parameter(Mandatory = $true)]
    [string] $OutPath,
    [Parameter(Mandatory = $true)]
    [string] $OutConfig,
    [ValidateSet("Standard", "Max")]
    [string] $Profile = "Standard"
)

$ErrorActionPreference = "Stop"

function Resolve-LatestRefPack([string] $PackRoot, [string] $RefSubPath) {
    if (-not (Test-Path $PackRoot)) {
        throw "Missing ref pack folder: $PackRoot"
    }
    $latest = Get-ChildItem $PackRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $full = Join-Path $latest.FullName $RefSubPath
    if (-not (Test-Path $full)) {
        throw "Expected ref path not found: $full"
    }
    return [System.IO.Path]::GetFullPath($full)
}

function Xml-Path([string] $p) {
    return $p.Replace("\", "/")
}

$wpfRoot = Join-Path $env:ProgramFiles "dotnet\packs\Microsoft.WindowsDesktop.App.Ref"
$netRoot = Join-Path $env:ProgramFiles "dotnet\packs\Microsoft.NETCore.App.Ref"
$wpfRef = Resolve-LatestRefPack $wpfRoot "ref\net8.0"
$netRef = Resolve-LatestRefPack $netRoot "ref\net8.0"

$inPath = [System.IO.Path]::GetFullPath($InPath.TrimEnd('\', '/'))
$outPath = [System.IO.Path]::GetFullPath($OutPath.TrimEnd('\', '/'))
$mainDll = Join-Path $inPath "Lockwell.dll"
if (-not (Test-Path $mainDll)) {
    throw "Lockwell.dll not found: $mainDll"
}

# Lockwell.Core carries every line of crypto, the vault format and the whole sync and
# pairing stack. It used to ship completely in the clear: it was never listed as a module
# here, so nothing in it was touched. It is obfuscated in the SAME pass as Lockwell.dll,
# which matters -- the two assemblies reference each other, and only a single pass renames
# those references consistently on both sides. Two separate passes would rename Core's
# members and leave Lockwell.dll calling the old names.
$coreDll = Join-Path $inPath "Lockwell.Core.dll"
$hasCore = Test-Path $coreDll

$skipNamespaceLines = @(
    # Desktop DTOs written to disk (the vault, settings, profiles, backup manifest). Their
    # member names ARE the on-disk JSON, so they are never renamed.
    "Lockwell.Views*",
    "Lockwell.Models*",
    "Lockwell.Helpers*"
) | ForEach-Object { "  <SkipNamespace name=`"$_`" />" }

# Source-generated JSON and startup paths must stay stable after Max obfuscation.
#
# Note what is NOT skipped: the crypto, the handshake, the key agreement and the pairing
# and transfer logic all get renamed and string-hidden. Only the shapes that cross a
# boundary are preserved, and they are preserved by two global rules rather than by
# skipping the code around them: RenameTypes=false and RenameProperties=false below mean
# every serialised property name -- on disk and on the wire to the phone -- survives even
# though the methods and fields beside it are renamed. So a beta desktop build still reads
# a vault written by 1.2.0, and still speaks the identical protocol to the Android app,
# whose copy of Core is compiled separately and unobfuscated.
$skipTypeLines = @(
    "Lockwell.MainWindow",
    "Lockwell.App",
    "Lockwell.Services.AppSettings",
    "Lockwell.AppSettingsJsonContext",
    "Lockwell.Services.VaultHeaderJsonContext",
    "Lockwell.Services.BackupManifestJsonContext",
    "Lockwell.Services.PreflightScanner",
    "Lockwell.Services.PreflightFinding",
    "Lockwell.Services.ThreatSeverity",
    "Lockwell.Services.ScreenCaptureDetector"
)

if ($hasCore) {
    # The source-generated JSON contexts in Core. Same treatment the desktop contexts get:
    # skipped whole, because the generator emits code whose shape the serializer depends on.
    $skipTypeLines += @(
        "Lockwell.Sync.SyncJson",
        "Lockwell.Sync.DiscoveryJson",
        "Lockwell.Sync.TransferJson",
        "Lockwell.Sync.TrustStoreJson",
        "Lockwell.Update.UpdateJson"
    )
}

$skipTypeLines = $skipTypeLines | ForEach-Object { "  <SkipType name=`"$_`" skipMethods=`"true`" skipFields=`"true`" skipProperties=`"true`" skipEvents=`"true`" skipStringHiding=`"true`" />" }

$skipJoined = ($skipNamespaceLines + $skipTypeLines) -join [Environment]::NewLine

$inXml = Xml-Path $inPath
$outXml = Xml-Path $outPath
$wpfXml = Xml-Path $wpfRef
$netXml = Xml-Path $netRef
$moduleXml = Xml-Path $mainDll
$coreModuleLine = if ($hasCore) { "  <Module file=`"$(Xml-Path $coreDll)`" />" } else { "" }

if ($Profile -eq "Max") {
    # WPF embeds BAML type IDs in the same assembly. Renaming types breaks startup
    # (XamlParseException: ResolveBamlType / NotImplementedException).
    $renameTypes = "false"
    $keepPublicApi = "false"
    $reuseNames = "false"
    $skipGenerated = "true"
    $skipSpecialName = "true"
    $comment = "Consumer GitHub release: max Obfuscar (no type rename; WPF-safe)."
} else {
    $renameTypes = "false"
    $keepPublicApi = "true"
    $reuseNames = "true"
    $skipGenerated = "false"
    $skipSpecialName = "false"
    $comment = "Consumer Lockwell: string hiding + method/field rename in Services."
}

$xml = @"
<?xml version='1.0'?>
<!-- $comment -->
<Obfuscator>
  <Var name="InPath" value="$inXml" />
  <Var name="OutPath" value="$outXml" />
  <Var name="KeepPublicApi" value="$keepPublicApi" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="SuppressIldasm" value="true" />
  <Var name="ReuseNames" value="$reuseNames" />
  <Var name="SkipGenerated" value="$skipGenerated" />
  <Var name="SkipSpecialName" value="$skipSpecialName" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="RenameFields" value="true" />
  <Var name="RenameMethods" value="true" />
  <Var name="RenameTypes" value="$renameTypes" />
  <AssemblySearchPath path="$wpfXml" />
  <AssemblySearchPath path="$netXml" />
$skipJoined
  <Module file="$moduleXml" />
$coreModuleLine
</Obfuscator>
"@

$dir = Split-Path $OutConfig -Parent
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Set-Content -Path $OutConfig -Value $xml -Encoding utf8
