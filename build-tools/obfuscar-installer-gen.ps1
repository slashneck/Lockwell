# Writes obfuscar-installer.gen.xml for LockwellSetup.dll (string hiding + Services rename; WPF-safe).
param(
    [Parameter(Mandatory = $true)]
    [string] $InPath,
    [Parameter(Mandatory = $true)]
    [string] $OutPath,
    [Parameter(Mandatory = $true)]
    [string] $OutConfig
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
$setupDll = Join-Path $inPath "LockwellSetup.dll"
if (-not (Test-Path $setupDll)) {
    throw "LockwellSetup.dll not found: $setupDll"
}

$skipTypeLines = @(
    "Lockwell.Installer.MainWindow",
    "Lockwell.Installer.App",
    "Lockwell.Installer.InstallerViewModel",
    "Lockwell.Installer.IntEqualsConverter",
    "Lockwell.Installer.BoolToInstallTitleConverter",
    "Lockwell.Installer.BoolToInstallSubtitleConverter",
    "Lockwell.Installer.StepDotBrushConverter"
) | ForEach-Object { "  <SkipType name=`"$_`" />" }

$skipJoined = $skipTypeLines -join [Environment]::NewLine

$inXml = Xml-Path $inPath
$outXml = Xml-Path $outPath
$wpfXml = Xml-Path $wpfRef
$netXml = Xml-Path $netRef
$moduleXml = Xml-Path $setupDll

$xml = @"
<?xml version='1.0'?>
<!-- LockwellSetup: string hiding + method/field rename (UI types skipped). -->
<Obfuscator>
  <Var name="InPath" value="$inXml" />
  <Var name="OutPath" value="$outXml" />
  <Var name="HideStrings" value="true" />
  <Var name="SuppressIldasm" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="RenameFields" value="true" />
  <Var name="RenameMethods" value="true" />
  <Var name="RenameTypes" value="false" />
  <AssemblySearchPath path="$wpfXml" />
  <AssemblySearchPath path="$netXml" />
$skipJoined
  <Module file="$moduleXml" />
</Obfuscator>
"@

$dir = Split-Path $OutConfig -Parent
if ($dir -and -not (Test-Path $dir)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Set-Content -Path $OutConfig -Value $xml -Encoding utf8
