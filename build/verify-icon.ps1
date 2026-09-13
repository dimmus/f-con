param([string]$Ico = "$PSScriptRoot/../src/FCon.App/Assets/fcon.ico", [string]$Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$bytes = [System.IO.File]::ReadAllBytes($Ico)
$count = [BitConverter]::ToInt16($bytes, 4)
Write-Output "directory declares $count image(s), file is $($bytes.Length) bytes"

$sizes = @()
for ($i = 0; $i -lt $count; $i++) {
    $o = 6 + (16 * $i)
    $wd = $bytes[$o]; $ht = $bytes[$o + 1]
    $len = [BitConverter]::ToInt32($bytes, $o + 8)
    $off = [BitConverter]::ToInt32($bytes, $o + 12)
    $end = $off + $len
    $ok = ($end -le $bytes.Length)
    $png = ($bytes[$off] -eq 0x89 -and $bytes[$off + 1] -eq 0x50)
    Write-Output ("  entry {0}: {1}x{2} len={3} off={4} inBounds={5} png={6}" -f $i, $wd, $ht, $len, $off, $ok, $png)
    if (-not $ok) { throw "entry $i runs past end of file" }
    $sizes += $(if ($wd -eq 0) { 256 } else { [int]$wd })
}

# Loading each size the way Windows does is the real test.
foreach ($s in $sizes) {
    $icon = New-Object System.Drawing.Icon($Ico, $s, $s)
    $bmp = $icon.ToBitmap()
    Write-Output ("  load {0}px -> {1}x{2}" -f $s, $bmp.Width, $bmp.Height)
    $bmp.Dispose(); $icon.Dispose()
}

if ($Preview) {
    $pad = 12
    $show = 16, 24, 32, 48, 64, 128
    $w = 0; foreach ($s in $show) { $w += $s + $pad }; $w += $pad
    $h = 128 + ($pad * 2)
    $out = New-Object System.Drawing.Bitmap([int]$w, [int]$h)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.Clear([System.Drawing.Color]::FromArgb(15, 17, 21))
    $x = $pad
    foreach ($s in $show) {
        $icon = New-Object System.Drawing.Icon($Ico, $s, $s)
        $bmp = $icon.ToBitmap()
        $g.DrawImageUnscaled($bmp, [int]$x, [int](($h - $s) / 2))
        $x += $s + $pad
        $bmp.Dispose(); $icon.Dispose()
    }
    $out.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $out.Dispose()
    Write-Output "preview -> $Preview"
}
Write-Output 'ICON OK'
