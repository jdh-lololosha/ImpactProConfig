# Портативная сборка: упаковывает publish/ в ImpactProConfig-vX.Y.Z-Portable.zip.
# MSI-установщик отменён: программа полностью Portable (exe + hidusb.dll рядом),
# никаких прав админа и тяжёлых ассетов в релизе.
param(
    [string]$PublishDir = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'

# Версию берём из csproj — единственный источник (как в MSI-эпоху:
# релиз, апдейтер и сборка обязаны говорить об одном и том же числе).
$csprojPath = Join-Path $PSScriptRoot 'ImpactProConfig.csproj'
[xml]$csproj = Get-Content -LiteralPath $csprojPath
# .InnerText, а не индексация: ("1.1.5")[0] даёт System.Char '1'.
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

$exe = Join-Path $PublishDir 'ImpactProConfig.exe'
if (-not (Test-Path $exe)) { throw "Publish dir not found or no ImpactProConfig.exe: $PublishDir" }
# hidusb.dll обязателен: без него протокол не загрузится (см. AGENTS.md).
if (-not (Test-Path (Join-Path $PublishDir 'hidusb.dll'))) { throw "hidusb.dll missing in publish dir!" }

$OutZip = Join-Path $PSScriptRoot "ImpactProConfig-v$productVersion-Portable.zip"

# Чистим старый архив, иначе Copy-Into-Archive оставляет прошлые файлы версии.
Remove-Item -LiteralPath $OutZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $OutZip -CompressionLevel Optimal

$zip = Get-Item -LiteralPath $OutZip
Write-Host ("ZIP_OK: {0} ({1:N0} bytes)" -f $zip.FullName, $zip.Length)
