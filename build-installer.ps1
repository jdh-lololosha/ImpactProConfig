# УСТАРЕЛО: MSI-установщик отменён (Program Files требовал права админа,
# архив был ~66 Мб, автообновление требовало UAC). Теперь релиз — это
# портативный ZIP: см. build-portable.ps1.
# Скрипт оставлен как заглушка, чтобы старые инструкции не падали.
Write-Host 'build-installer.ps1: MSI discontinued, building portable ZIP instead...'
& (Join-Path $PSScriptRoot 'build-portable.ps1') @args
