# Генерирует installer/vpnus.ico (256x256 PNG-compressed ICO) без внешних зависимостей.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$iconDir = Join-Path $root 'installer'
$target = Join-Path $iconDir 'vpnus.ico'
if (-not (Test-Path $iconDir)) { New-Item -ItemType Directory -Path $iconDir | Out-Null }

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$rect = New-Object System.Drawing.Rectangle 8, 8, ($size - 16), ($size - 16)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $rect,
    [System.Drawing.Color]::FromArgb(255, 91, 140, 255),
    [System.Drawing.Color]::FromArgb(255, 124, 92, 255),
    45.0)
$g.FillEllipse($brush, $rect)

$font = New-Object System.Drawing.Font 'Segoe UI', 130, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$g.DrawString('V', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $size, $size), $format)
$g.Dispose()

$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose()
$bmp.Dispose()

$fs = [System.IO.File]::Create($target)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)      # reserved
$bw.Write([UInt16]1)      # type: icon
$bw.Write([UInt16]1)      # count
$bw.Write([Byte]0)        # width 256
$bw.Write([Byte]0)        # height 256
$bw.Write([Byte]0)        # palette
$bw.Write([Byte]0)        # reserved
$bw.Write([UInt16]1)      # planes
$bw.Write([UInt16]32)     # bpp
$bw.Write([UInt32]$png.Length)
$bw.Write([UInt32]22)     # offset
$bw.Write($png)
$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Write-Host "icon: $target ($([Math]::Round($png.Length / 1024, 1)) КБ)"
