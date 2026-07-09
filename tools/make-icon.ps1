# Generates LoadView.ico (repo root) from the same three-bar glyph the tray icon draws.
# Multi-size, PNG-compressed frames (valid on Windows Vista+). Run once; commit the .ico.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $f = $s / 32.0

    $bars = @(
        @(4, 16, 6, 12, [System.Drawing.Color]::FromArgb(0x4F, 0x8C, 0xFF)),
        @(13, 9, 6, 19, [System.Drawing.Color]::FromArgb(0x36, 0xC7, 0x9B)),
        @(22, 4, 6, 24, [System.Drawing.Color]::FromArgb(0xFF, 0x9F, 0x40))
    )
    foreach ($b in $bars) {
        $brush = New-Object System.Drawing.SolidBrush($b[4])
        $g.FillRectangle($brush, [float]($b[0]*$f), [float]($b[1]*$f), [float]($b[2]*$f), [float]($b[3]*$f))
        $brush.Dispose()
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , ($ms.ToArray())
    $ms.Dispose()
}

$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'LoadView.ico'
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$frames.Count)     # image count

$offset = 6 + 16 * $frames.Count
for ($i = 0; $i -lt $frames.Count; $i++) {
    $s = $sizes[$i]
    $len = $frames[$i].Length
    $dim = $(if ($s -ge 256) { 0 } else { $s })
    $bw.Write([byte]$dim)            # width  (0 = 256)
    $bw.Write([byte]$dim)            # height (0 = 256)
    $bw.Write([byte]0)               # palette count
    $bw.Write([byte]0)               # reserved
    $bw.Write([UInt16]1)             # planes
    $bw.Write([UInt16]32)            # bpp
    $bw.Write([UInt32]$len)          # bytes in resource
    $bw.Write([UInt32]$offset)       # offset
    $offset += $len
}
foreach ($frame in $frames) { $bw.Write($frame) }

$bw.Flush(); $bw.Close(); $fs.Close()
Write-Host ("Wrote {0} ({1} bytes, {2} frames)" -f $out, (Get-Item $out).Length, $frames.Count)
