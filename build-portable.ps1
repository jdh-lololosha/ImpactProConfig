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

# ===== Официальный Raw Accel рядом с программой =====
# Вкладка «Акселерация» ставит драйвер из Drivers\RawAccel\, поэтому
# официальный архив кладём в portable-сборку заранее: вкладка должна работать
# без первого выхода в интернет и без UAC на этапе скачивания.
#
# Файлы копируются БАЙТ В БАЙТ и не патчатся: rawaccel.sys подписан Microsoft,
# любая правка ломает подпись, а неподписанный драйвер не загрузится вообще.
# Подпись проверяется здесь же, на этапе сборки: если апстрим сменит издателя
# или отдаст битый файл, сборка падает, а не молча пакует непригодный архив.
$RawAccelDest = Join-Path $PublishDir 'Drivers\RawAccel'
$RawAccelZip  = Join-Path $PSScriptRoot 'RawAccel_v1.7.1.zip'

if (-not (Test-Path $RawAccelZip)) {
    # Сообщение ASCII-only намеренно: build-portable.ps1 лежит БЕЗ UTF-8 BOM,
    # поэтому PowerShell 5.1 читает его как ANSI и кириллица в строковом
    # литерале превращается в мусор, который рвёт разбор (ParserError на
    # UnexpectedToken). Комментарии с кириллицей безопасны — они не парсятся
    # как токены, а вот строки трогать нельзя.
    throw "Raw Accel archive not found: $RawAccelZip -- download official release from https://github.com/RawAccelOfficial/rawaccel/releases"
}

Write-Host "RAWACCEL: unpacking $(Split-Path $RawAccelZip -Leaf)"
if (Test-Path $RawAccelDest) { Remove-Item $RawAccelDest -Recurse -Force }
$extract = Join-Path $env:TEMP ("rawaccel-build-" + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $RawAccelZip -DestinationPath $extract -Force
    # В архиве единственная папка RawAccel\ — поднимаем её уровнем выше,
    # иначе получится Drivers\RawAccel\RawAccel\rawaccel.exe и пути в
    # настройках разъедутся.
    $src = Join-Path $extract 'RawAccel'
    if (-not (Test-Path $src)) { throw "Unexpected archive layout: no RawAccel\ folder in $RawAccelZip" }
    New-Item -ItemType Directory -Force -Path $RawAccelDest | Out-Null
    Copy-Item -Path (Join-Path $src '*') -Destination $RawAccelDest -Recurse -Force

    # Обязательные файлы: без них установка и применение настроек невозможны.
    foreach ($need in 'installer.exe', 'uninstaller.exe', 'rawaccel.exe',
                     'writer.exe', 'wrapper.dll', 'driver\rawaccel.sys') {
        $p = Join-Path $RawAccelDest $need
        if (-not (Test-Path $p)) { throw "RawAccel archive is incomplete: missing $need" }
    }

    # Подпись драйвера. Сертификат WHCP у текущего релиза просрочен
    # (notAfter 2025-10-08), поэтому просрочку НЕ считаем ошибкой сборки —
    # иначе собрать было бы нельзя. Независимый не-Microsoft издатель или
    # отсутствие подписи — вот что должно валить сборку.
    $sys = Join-Path $RawAccelDest 'driver\rawaccel.sys'
    $sig = Get-AuthenticodeSignature -FilePath $sys
    Write-Host ("RAWACCEL: rawaccel.sys status={0} subject={1}" -f $sig.Status, $sig.SignerCertificate.Subject)
    if ($sig.Status -eq 'NotSigned') {
        throw "rawaccel.sys has NO signature - refusing to package it"
    }
    if ($sig.Status -ne 'Valid' -and $sig.SignerCertificate.Subject -notmatch 'Microsoft') {
        throw "rawaccel.sys signed by unexpected publisher: $($sig.SignerCertificate.Subject)"
    }
} finally {
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue }
}
# hidusb.dll обязателен: без него протокол не загрузится (см. AGENTS.md).
if (-not (Test-Path (Join-Path $PublishDir 'hidusb.dll'))) { throw "hidusb.dll missing in publish dir!" }
# Updater.exe обязателен: без него автообновление не сможет заменить файлы
# запущенного exe (см. UpdateService.DownloadAndUpdateAsync). Его собирает
# цель PublishUpdater основного csproj при `dotnet publish`.
if (-not (Test-Path (Join-Path $PublishDir 'Updater.exe'))) { throw "Updater.exe missing in publish dir! (dotnet publish основного проекта собирает его в Updater\)" }

$OutZip = Join-Path $PSScriptRoot "ImpactProConfig-v$productVersion-Portable.zip"

# Чистим старый архив, иначе Copy-Into-Archive оставляет прошлые файлы версии.
Remove-Item -LiteralPath $OutZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $OutZip -CompressionLevel Optimal

$zip = Get-Item -LiteralPath $OutZip
Write-Host ("ZIP_OK: {0} ({1:N0} bytes)" -f $zip.FullName, $zip.Length)
