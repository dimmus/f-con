<#
.SYNOPSIS
    Generates the KVN application icon as a multi-resolution .ico.

.DESCRIPTION
    The mark matches the badge in the app's navigation rail: an accent-blue rounded
    square with a white K. Each size is rendered separately rather than scaled from
    one bitmap, so the 16px entry stays legible instead of turning to mush.

    Sizes below 256 are written as uncompressed 32bpp DIBs and only 256 uses PNG.
    That is the conventional layout: PNG-compressed entries are fine for the Windows
    shell but System.Drawing cannot decode them reliably, and an icon should not
    depend on which decoder happens to load it.

    Re-run only when the mark changes; the .ico is committed as a build asset.
#>
param([string]$Out = "$PSScriptRoot/../src/FCon.App/Assets/fcon.ico")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$accent = [System.Drawing.Color]::FromArgb(76, 141, 255)

function New-Mark([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, inset slightly so the corners are not clipped by the canvas.
    $pad = [Math]::Max(1, [int]($s * 0.06))
    $side = $s - (2 * $pad)
    $radius = [Math]::Max(2, [int]($side * 0.24))
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $side - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $side - $d, $pad + $side - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.SolidBrush($accent)
    $g.FillPath($brush, $path)

    $fontSize = [float]($side * 0.62)
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'
    $fmt.LineAlignment = 'Center'
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, $side, $side)
    $g.DrawString('K', $font, $white, $rect, $fmt)

    $brush.Dispose(); $white.Dispose(); $font.Dispose(); $fmt.Dispose()
    $path.Dispose(); $g.Dispose()
    return $bmp
}

# 32bpp DIB: header, then bottom-up BGRA rows, then a padded 1bpp AND mask.
function ConvertTo-Dib([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([int]40)          # biSize
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))    # height covers XOR image plus AND mask
    $bw.Write([int16]1)         # biPlanes
    $bw.Write([int16]32)        # biBitCount
    $bw.Write([int]0)           # BI_RGB
    $bw.Write([int]0)           # biSizeImage
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($w * 4)
        for ($y = $h - 1; $y -ge 0; $y--) {
            $src = [IntPtr]::Add($data.Scan0, $y * $data.Stride)
            [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $row.Length)
            $bw.Write($row)
        }
    }
    finally { $bmp.UnlockBits($data) }

    # Alpha already carries transparency, so the mask is all zeros - but the rows
    # still have to be there, padded to a 4-byte boundary.
    $maskStride = [int]([Math]::Floor(($w + 31) / 32)) * 4
    $blank = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($blank) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    # Comma keeps PowerShell from unrolling the array into individual bytes.
    return , $bytes
}

$images = @()
foreach ($s in $sizes) {
    $bmp = New-Mark $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $payload = $ms.ToArray()
        $ms.Dispose()
    }
    else {
        $payload = ConvertTo-Dib $bmp
    }
    $images += , @{ Size = $s; Bytes = $payload }
    $bmp.Dispose()
}

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($p in $images) {
    $dim = $(if ($p.Size -ge 256) { 0 } else { $p.Size })
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$p.Bytes.Length)
    $w.Write([int]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $images) { $w.Write([byte[]]$p.Bytes) }

$w.Flush(); $w.Dispose(); $fs.Dispose()
Write-Output ("wrote {0} ({1} sizes, {2:N0} bytes)" -f $Out, $images.Count, (Get-Item $Out).Length)
