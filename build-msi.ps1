# ╨У╨╡╨╜╨╡╤А╨╕╤А╤Г╨╡╤В .wxs ╨┤╨╗╤П WiX v4/v5 ╨╕╨╖ ╤Б╨╛╨┤╨╡╤А╨╢╨╕╨╝╨╛╨│╨╛ publish-╨┐╨░╨┐╨║╨╕ ╨╕ ╤Б╨╛╨▒╨╕╤А╨░╨╡╤В MSI.
# ╨Я╨╛╨║╤А╤Л╨▓╨░╨╡╤В ╨┐╨╛╨┤╨┐╨░╨┐╨║╨╕ (╨╡╤Б╨╗╨╕ ╨┐╨╛╤П╨▓╤П╤В╤Б╤П), ╤П╤А╨╗╤Л╨║╨╕ ╨╜╨░ ╤А╨░╨▒╨╛╤З╨╡╨╝ ╤Б╤В╨╛╨╗╨╡ ╨╕ ╨▓ ╨╝╨╡╨╜╤О ╨Я╤Г╤Б╨║.
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'

# ╨Т╨╡╤А╤Б╨╕╤О ╨▒╨╡╤А╤С╨╝ ╨╕╨╖ csproj тАФ ╨╡╨┤╨╕╨╜╤Б╤В╨▓╨╡╨╜╨╜╤Л╨╣ ╨╕╤Б╤В╨╛╤З╨╜╨╕╨║. ╨а╨░╨╜╤М╤И╨╡ ╨╛╨╜╨░ ╨▒╤Л╨╗╨░ ╨▓╨┐╨╕╤Б╨░╨╜╨░
# ╨╖╨┤╨╡╤Б╤М ╨╕ ╨▓ .wxs ╨▓╤А╤Г╤З╨╜╤Г╤О, ╨╕╨╖-╨╖╨░ ╤З╨╡╨│╨╛ MSI ╤Б╨╛╨▒╨╕╤А╨░╨╗╤Б╤П ╤Б╨╛ ╤Б╤В╨░╤А╨╛╨╣ ╨▓╨╡╤А╤Б╨╕╨╡╨╣,
# ╨╕ ╨░╨▓╤В╨╛╨╛╨▒╨╜╨╛╨▓╨╗╨╡╨╜╨╕╨╡ ╨╜╨╡ ╤Б╤А╨░╨▒╨░╤В╤Л╨▓╨░╨╗╨╛ (╨╜╨╛╨▓╤Л╨╣ msi ╨╜╨╡ ╨▓╨╕╨┤╨╡╨╗ ╤Б╤В╨░╤А╤Л╨╣ ╨┐╤А╨╛╨┤╤Г╨║╤В).
$csprojPath = Join-Path $PSScriptRoot 'ImpactProConfig.csproj'
[xml]$csproj = Get-Content -LiteralPath $csprojPath
# ╨Т╨Р╨Ц╨Э╨Ю: .InnerText, ╨░ ╨╜╨╡ ╨╕╨╜╨┤╨╡╨║╤Б╨░╤Ж╨╕╤П. ("1.1.0")[0] ╨┤╨░╤С╤В System.Char '1',
# ╨╕ ╨╕╨╖ ╤Н╤В╨╛╨│╨╛ ╨┐╨╛╨╗╤Г╤З╨░╨╗╤Б╤П MSI ╨▓╨╕╨┤╨░ "v1-Setup" тАФ ╨▓╨╡╤А╤Б╨╕╤П ╨╝╨╛╨╗╤З╨░ ╤В╨╡╤А╤П╨╗╨░ ╨▓╤Б╤С ╨┐╨╛╤Б╨╗╨╡ ╤В╨╛╤З╨║╨╕.
# ╨н╨╗╨╡╨╝╨╡╨╜╤В╤Л ╨▒╤Л╨▓╨░╤О╤В ╨╕ XmlElement, ╨╕ ╤Б╤В╤А╨╛╨║╨╛╨╣ (PowerShell ╤Б╨╜╨╕╨╝╨░╨╡╤В ╤В╨╕╨┐ ╤Б ╨┐╤А╨╛╤Б╤В╤Л╤Е
# ╤Г╨╖╨╗╨╛╨▓), ╨┐╨╛╤Н╤В╨╛╨╝╤Г ╨▒╨╡╤А╤С╨╝ ╤В╨╡╨║╤Б╤В ╤В╨╡╤А╨┐╨╕╨╝╨╛, ╨▒╨╡╨╖ ╨╛╨▒╤А╨░╤Й╨╡╨╜╨╕╤П ╨║ .InnerText ╨▓ ╤Д╨╕╨╗╤М╤В╤А╨╡.
$versionText = $csproj.Project.PropertyGroup.Version |
    Where-Object { if ($_ -is [string]) { $_.Trim() } else { $_ -and $_.InnerText.Trim() } } |
    Select-Object -First 1
if (-not $versionText) { throw "Version not found in $csprojPath" }

if ($versionText -is [string]) { $productVersion = $versionText }
else { $productVersion = $versionText.InnerText }
$productVersion = $productVersion.Trim() -replace '^v', ''
if ($productVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$productVersion' is not strict SemVer (expected X.Y.Z)"
}
$OutMsi = Join-Path $PSScriptRoot "ImpactProConfig-v$productVersion-Setup.msi"

$exe = Join-Path $PublishDir 'ImpactProConfig.exe'
if (-not (Test-Path $exe)) { throw "Publish dir not found or no ImpactProConfig.exe: $PublishDir" }
if (-not (Test-Path (Join-Path $PublishDir 'hidusb.dll'))) { throw "hidusb.dll missing in publish dir!" }
# Updater.exe — процесс-обновитель (цель PublishUpdater основного csproj).
if (-not (Test-Path (Join-Path $PublishDir 'Updater.exe'))) { throw "Updater.exe missing in publish dir!" }

function New-DeterministicGuid([string]$s) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s.ToLowerInvariant()))
    return ([System.Guid]::new($bytes)).ToString('B')
}
function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }

# ---- ╨б╨▒╨╛╤А ╨┤╨░╨╜╨╜╤Л╤Е ╨╛ ╤Д╨░╨╣╨╗╨░╤Е/╨┐╨░╨┐╨║╨░╤Е ----
$root = (Resolve-Path $PublishDir).Path.TrimEnd('\')
$rows = Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($root.Length).TrimStart('\')
    $dir = if ($rel.Contains('\')) { $rel.Substring(0, $rel.LastIndexOf('\')) } else { '' }
    [pscustomobject]@{ Rel = $rel; Dir = $dir; Full = $_.FullName }
}

$dirIds     = @{ '' = 'INSTALLFOLDER' }   # dir rel -> xml Id
$childrenOf = @{}                          # parent dir -> [child dirs]
$fileByDir  = @{}                          # dir -> rows

foreach ($row in $rows) {
    # ╨Т╨Р╨Ц╨Э╨Ю: @(...) ╨╛╨▒╤П╨╖╨░╤В╨╡╨╗╨╡╨╜ тАФ ╨▒╨╡╨╖ ╨╜╨╡╨│╨╛ if ╨▓╨╛╨╖╨▓╤А╨░╤Й╨░╨╡╤В ╤Б╨║╨░╨╗╤П╤А "cs",
    # ╨╕ $parts[0] ╨┤╨░╤С╤В [char]'c' (╨┐╨╡╤А╨▓╤Л╨╣ ╤Б╨╕╨╝╨▓╨╛╨╗), ╨░ ╨╜╨╡ ╤Б╤В╤А╨╛╨║╤Г ╨║╨░╤В╨░╨╗╨╛╨│╨░.
    $parts = @()
    if ($row.Dir -ne '') { $parts = @($row.Dir -split '\\') }
    $cur = ''
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $child = if ($i -eq 0) { $parts[0] } else { "$cur\$($parts[$i])" }
        if (-not $dirIds.ContainsKey($child)) {
            $dirIds[$child] = 'D_{0}' -f $dirIds.Count
            if (-not $childrenOf.ContainsKey($cur)) { $childrenOf[$cur] = New-Object System.Collections.Generic.List[string] }
            $childrenOf[$cur].Add($child)
        }
        $cur = $child
    }
    if (-not $fileByDir.ContainsKey($row.Dir)) { $fileByDir[$row.Dir] = New-Object System.Collections.Generic.List[object] }
    $fileByDir[$row.Dir].Add($row)
}

# ---- ╨а╨╡╨║╤Г╤А╤Б╨╕╨▓╨╜╨░╤П ╨│╨╡╨╜╨╡╤А╨░╤Ж╨╕╤П XML ----
$script:idx = 0
$script:refs = New-Object System.Collections.Generic.List[string]

function Write-DirXml([string]$d, [string]$indent) {
    $sb = New-Object System.Text.StringBuilder
    if ($fileByDir.ContainsKey($d)) {
        foreach ($row in $fileByDir[$d]) {
            $script:idx++
            $cid = 'C_F{0}' -f $script:idx
            $fid = 'F{0}' -f $script:idx
            $guid = New-DeterministicGuid $row.Rel
            [void]$sb.AppendLine("$indent<Component Id=`"$cid`" Guid=`"$guid`">")
            [void]$sb.AppendLine("$indent  <File Id=`"$fid`" Source=`"$(Esc $row.Full)`" />")
            [void]$sb.AppendLine("$indent</Component>")
            $script:refs.Add($cid)
        }
    }
    if ($childrenOf.ContainsKey($d)) {
        foreach ($c in ($childrenOf[$d] | Sort-Object)) {
            $name = $c.Substring($c.LastIndexOf('\') + 1)
            [void]$sb.AppendLine("$indent<Directory Id=`"$(Esc $dirIds[$c])`" Name=`"$(Esc $name)`">")
            [void]$sb.Append((Write-DirXml $c ($indent + '  ')))
            [void]$sb.AppendLine("$indent</Directory>")
        }
    }
    return $sb.ToString()
}

$inner = Write-DirXml '' '      '

# ---- ╨п╤А╨╗╤Л╨║╨╕ ----
$desktopGuid = '{5C2E9F7A-1B34-4D6E-8A0C-2F7D9E4B6A31}'
$startGuid   = '{7B4D2A19-6E52-4C8F-9D3B-1A6E5C8F0B72}'
$script:refs.Add('C_DesktopShortcut')
$script:refs.Add('C_StartMenuShortcut')

$shortcuts = @"
    <StandardDirectory Id="DesktopFolder">
      <Component Id="C_DesktopShortcut" Guid="$desktopGuid">
        <Shortcut Id="SC_Desktop" Name="ARDOR GAMING Impact PRO" Target="[INSTALLFOLDER]ImpactProConfig.exe" WorkingDirectory="INSTALLFOLDER" />
        <RegistryValue Root="HKLM" Key="Software\ARDOR GAMING\ImpactProConfig" Name="DesktopShortcut" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </StandardDirectory>
    <StandardDirectory Id="ProgramMenuFolder">
      <Component Id="C_StartMenuShortcut" Guid="$startGuid">
        <Shortcut Id="SC_Start" Name="ARDOR GAMING Impact PRO" Target="[INSTALLFOLDER]ImpactProConfig.exe" WorkingDirectory="INSTALLFOLDER" />
        <RegistryValue Root="HKLM" Key="Software\ARDOR GAMING\ImpactProConfig" Name="StartMenuShortcut" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </StandardDirectory>
"@

$featureRefs = ($script:refs | ForEach-Object { "      <ComponentRef Id=`"$_`" />" }) -join "`n"

# UpgradeCode ╨Э╨Х ╨╖╨░╨▓╨╕╤Б╨╕╤В ╨╛╤В ╨▓╨╡╤А╤Б╨╕╨╕: ╨╛╨╜ ╨┤╨╛╨╗╨╢╨╡╨╜ ╨╛╤Б╤В╨░╨▓╨░╤В╤М╤Б╤П ╨┐╤А╨╡╨╢╨╜╨╕╨╝, ╨╕╨╜╨░╤З╨╡
# ╤Г╤Б╤В╨░╨╜╨╛╨▓╤Й╨╕╨║ ╨┐╨╡╤А╨╡╤Б╤В╨░╨╜╨╡╤В ╨▓╨╕╨┤╨╡╤В╤М ╤Г╨╢╨╡ ╤Г╤Б╤В╨░╨╜╨╛╨▓╨╗╨╡╨╜╨╜╤Л╨╣ ╨┐╤А╨╛╨┤╤Г╨║╤В ╨║╨░╨║ ╤Б╨▓╨╛╤О ╨░╨┐╨│╤А╨╡╨╣╨┤-╨▓╨╡╤А╤Б╨╕╤О.
$upgradeGuid = New-DeterministicGuid 'ImpactProConfig-upgrade-v1'

# ---- ╨Ш╤В╨╛╨│╨╛╨▓╤Л╨╣ .wxs ----
$wxs = @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="ARDOR GAMING Impact PRO Config"
           Version="$productVersion"
           Manufacturer="ARDOR GAMING"
           UpgradeCode="$upgradeGuid"
           Scope="perMachine">
    <MajorUpgrade DowngradeErrorMessage="A newer version of ImpactProConfig is already installed." />
    <MediaTemplate EmbedCab="yes" />
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="ImpactProConfig">
$inner      </Directory>
    </StandardDirectory>
$shortcuts
    <Feature Id="Main" Title="ImpactProConfig" Level="1">
$featureRefs
    </Feature>
  </Package>
</Wix>
"@

$wxsPath = Join-Path $PSScriptRoot 'build\installer.wxs'
New-Item -ItemType Directory -Force -Path (Split-Path $wxsPath) | Out-Null
# WiX ╨╛╨╢╨╕╨┤╨░╨╡╤В UTF-8 (╨▓ ╤В.╤З. ╤Б BOM) тАФ ╨╖╨░╨┐╨╕╤Б╤Л╨▓╨░╨╡╨╝ ╤П╨▓╨╜╨╛.
[System.IO.File]::WriteAllText($wxsPath, $wxs, (New-Object System.Text.UTF8Encoding($true)))

# ---- ╨Я╨╛╨╕╤Б╨║ wix ----
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    Write-Host 'wix not found in PATH, installing...'
    dotnet tool install --global wix
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool install wix failed' }
    $wixExe = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
} else {
    $wixExe = $wixCmd.Source
}
if (-not (Test-Path $wixExe)) { $wixExe = 'wix' }

# ---- ╨б╨▒╨╛╤А╨║╨░ ----
$buildDir = Join-Path $PSScriptRoot 'build'
Push-Location $buildDir
try {
    & $wixExe build -arch x64 $wxsPath -o $OutMsi 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($code -ne 0) { throw "wix build failed with exit code $code" }
$msi = Get-Item $OutMsi
Write-Host ("MSI_OK: {0} ({1:N0} bytes)" -f $msi.FullName, $msi.Length)
