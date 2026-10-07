# Генерирует Assets/app.ico — иконка приложения ImpactProConfig.
# Дизайн: тёмное скруглённое поле (#151515), рамка Ardor red (#E81123),
# белый силуэт мыши с красным колесом. Рисуется GDI+ в системе координат
# 256x256 и уменьшается для всех размеров — мелкие размеры читаются.
# Повторный запуск перезаписывает .ico (иконка воспроизводима из скрипта).
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $root 'Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outIco = Join-Path $outDir 'app.ico'
$preview = Join-Path $outDir 'app-preview.png'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Всё рисуем в системе 256x256 и масштабируем в целевой размер.
    $k = $size / 256.0
    $g.ScaleTransform($k, $k)

    $bg    = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x15, 0x15, 0x15))
    $red   = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xE8, 0x11, 0x23))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xED, 0xED, 0xED))
    $dark  = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x15, 0x15, 0x15), 7)
    $redPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0xE8, 0x11, 0x23), 10)

    # 1. Скруглённое поле с красной рамкой.
    $r = 56
    $rect = New-Object System.Drawing.RectangleF(8, 8, 240, 240)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bg, $path)
    $g.DrawPath($redPen, $path)

    # 2. Силуэт мыши: скруглённый верх, чуть суженный низ.
    $mouse = New-Object System.Drawing.Drawing2D.GraphicsPath
    $mouse.AddArc(78, 46, 100, 100, 180, 180)              # верхний купол
    $mouse.AddArc(78, 146, 100, 76, 0, 180)                # низ
    $mouse.CloseFigure()
    $g.FillPath($white, $mouse)

    # 3. Разделитель кнопок (вертикальная щель верхней половины).
    $g.DrawLine($dark, 128, 52, 128, 126)

    # 4. Колесо — красный штрих в щели.
    $g.FillRectangle($red, 120, 64, 16, 44)

    # 5. Красная точка-индикатор внизу корпуса (подсветка статуса).
    $g.FillEllipse($red, 118, 178, 20, 20)

    $pen = $null
    $g.Dispose()
    return $bmp
}

# ---- Сборка ICO (PNG-записи, поддерживается с Vista) ----
# ВАЖНО: List[byte[]], а не @() += — PowerShell сплющивает byte[] в скаляры,
# и в файл уезжал 1 байт на размер вместо PNG-данных.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$pngs.Add($ms.ToArray())
    if ($s -eq 256) { $b.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose()
}

$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                      # reserved
$bw.Write([uint16]1)                      # type: icon
$bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)                 # width (0 = 256)
    $bw.Write([byte]$dim)                 # height
    $bw.Write([byte]0)                    # palette
    $bw.Write([byte]0)                    # reserved
    $bw.Write([uint16]1)                  # planes
    $bw.Write([uint16]32)                 # bpp
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $fs.ToArray())
$bw.Dispose(); $fs.Dispose()

$ico = Get-Item $outIco
Write-Host ("ICON_OK: {0} ({1:N0} bytes) preview={2}" -f $ico.FullName, $ico.Length, $preview)
