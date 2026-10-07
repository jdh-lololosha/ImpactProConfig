# Создаёт GitHub release v1.0.0 через REST API и прикрепляет MSI-файл.
param(
    [string]$MsiPath   = (Join-Path $PSScriptRoot 'ImpactProConfig-v1.0.0-Setup.msi'),
    [string]$Token     = $env:GH_TOKEN,
    [string]$Repo      = 'jdh-lololosha/ImpactProConfig',
    [string]$Tag       = 'v1.0.0',
    [string]$Title     = 'v1.0.0 - Official Release',
    [string]$Notes     = 'First official release with full hardware support, custom UI, OSD and MSI installer.'
)

$ErrorActionPreference = 'Stop'
if (-not $Token) { throw 'No token (GH_TOKEN)' }
if (-not (Test-Path $MsiPath)) { throw "MSI not found: $MsiPath" }

# Windows PowerShell 5.1 по умолчанию может не включать TLS1.2.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$headers = @{
    Authorization           = "Bearer $Token"
    Accept                  = 'application/vnd.github+json'
    'X-GitHub-Api-Version'  = '2022-11-28'
    'User-Agent'            = 'ImpactProConfig-release-script'
}

# 1. Есть ли уже релиз с таким тегом?
$release = $null
try {
    $release = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$Repo/releases/tags/$Tag" -Headers $headers
    Write-Host "RELEASE_EXISTS id=$($release.id)"
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
}

# 2. Создать, если нет.
if (-not $release) {
    $body = @{ tag_name = $Tag; name = $Title; body = $Notes; draft = $false; prerelease = $false } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" -Headers $headers -ContentType 'application/json' -Body $body
    Write-Host "RELEASE_CREATED id=$($release.id) url=$($release.html_url)"
} else {
    Write-Host 'RELEASE_REUSED'
}

# 3. Удалить старый ассет с тем же именем (идемпотентность).
$assetName = Split-Path $MsiPath -Leaf
foreach ($a in @($release.assets)) {
    if ($a.name -eq $assetName) {
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/$Repo/releases/assets/$($a.id)" -Headers $headers | Out-Null
        Write-Host "ASSET_DELETED old id=$($a.id)"
    }
}

# 4. Загрузить MSI.
$uploadUrl = ($release.upload_url -split '\{')[0]
$size = (Get-Item $MsiPath).Length
$resp = Invoke-RestMethod -Method Post `
    -Uri "$uploadUrl`?name=$([Uri]::EscapeDataString($assetName))" `
    -Headers $headers `
    -ContentType 'application/octet-stream' `
    -InFile $MsiPath
Write-Host "ASSET_UPLOADED id=$($resp.id) name=$($resp.name) size=$($resp.size) (expected=$size)"
if ($resp.size -ne $size) { throw "Asset size mismatch: $($resp.size) != $size" }

Write-Host "RELEASE_OK $($release.html_url)"
