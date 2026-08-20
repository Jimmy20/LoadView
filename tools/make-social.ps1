# Regenerates docs/social.png (the GitHub social preview / link card) from the current screenshot.
# Note: PowerShell variables are case-insensitive, so the card size uses names that cannot collide
# with the image size ($W vs $w was a bug that put the screenshot at x = -96).
Add-Type -AssemblyName System.Drawing

$cardW = 1200; $cardH = 630
$repo = 'C:\GitHubProjects\LoadView'
$shot = Join-Path $repo 'docs\Screenshot.png'

# Written as char codes so this script does not depend on how PowerShell guesses its own encoding.
$dot = [string][char]0x00B7   # middle dot
$deg = [string][char]0x00B0   # degree sign
$sep = "  $dot  "

$bg     = [System.Drawing.Color]::FromArgb(0x0E, 0x0E, 0x11)
$ink    = [System.Drawing.Color]::FromArgb(0xE8, 0xE8, 0xED)
$dim    = [System.Drawing.Color]::FromArgb(0x9A, 0x9A, 0xA2)
$accent = [System.Drawing.Color]::FromArgb(0x4F, 0x8C, 0xFF)
$border = [System.Drawing.Color]::FromArgb(0x2A, 0x2A, 0x31)

$bmp = New-Object System.Drawing.Bitmap($cardW, $cardH)
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$gfx.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$gfx.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$gfx.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

$gfx.Clear($bg)
$gfx.FillRectangle((New-Object System.Drawing.SolidBrush($accent)), 0, 0, 6, $cardH)

$fTitle = New-Object System.Drawing.Font('Segoe UI', 46, [System.Drawing.FontStyle]::Bold)
$fTag   = New-Object System.Drawing.Font('Segoe UI', 19)
$fLine  = New-Object System.Drawing.Font('Segoe UI', 16)
$fFoot  = New-Object System.Drawing.Font('Segoe UI', 14)

$bInk = New-Object System.Drawing.SolidBrush($ink)
$bDim = New-Object System.Drawing.SolidBrush($dim)
$bAcc = New-Object System.Drawing.SolidBrush($accent)

$tx = 76
$gfx.DrawString('LoadView', $fTitle, $bInk, $tx, 78)
$gfx.DrawString("Task Manager's performance graphs, pinned to your screen.", $fTag, $bAcc, ($tx + 4), 168)

$ty = 250
$gfx.DrawString(('Live  CPU' + $sep + 'GPU' + $sep + 'RAM' + $sep + 'Disk' + $sep + 'Network  graphs'),
    $fLine, $bInk, ($tx + 4), $ty)
$ty += 42
$gfx.DrawString(('Temperatures per disk' + $sep + 'Fan speeds' + $sep + 'Top processes' + $sep + 'Drives' + $sep + 'IP'),
    $fLine, $bInk, ($tx + 4), $ty)
$ty += 42
$gfx.DrawString(('Dark / light theme' + $sep + 'reorderable' + $sep + 'colours & alerts' + $sep +
    'MB/s or Mbps' + $sep + $deg + 'C/' + $deg + 'F'), $fLine, $bDim, ($tx + 4), $ty)

$gfx.DrawString(('Single portable exe' + $sep + 'Windows 10 / 11' + $sep + 'Free & open-source (MIT)'),
    $fFoot, $bDim, ($tx + 4), 470)
$gfx.DrawString('github.com/Jimmy20/LoadView', $fFoot, $bAcc, ($tx + 4), 516)

# The overlay itself on the right, in a rounded frame, scaled to fit the card height.
$img = [System.Drawing.Image]::FromFile($shot)
$imgH = 550
$scale = $imgH / [double]$img.Height
$imgW = [int]($img.Width * $scale)
$ix = $cardW - $imgW - 110
$iy = [int](($cardH - $imgH) / 2)

$pad = 12; $r = 18
$fx = $ix - $pad; $fy = $iy - $pad; $fw = $imgW + 2 * $pad; $fh = $imgH + 2 * $pad
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($fx, $fy, 2*$r, 2*$r, 180, 90)
$path.AddArc($fx + $fw - 2*$r, $fy, 2*$r, 2*$r, 270, 90)
$path.AddArc($fx + $fw - 2*$r, $fy + $fh - 2*$r, 2*$r, 2*$r, 0, 90)
$path.AddArc($fx, $fy + $fh - 2*$r, 2*$r, 2*$r, 90, 90)
$path.CloseFigure()
$gfx.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x1A, 0x1A, 0x1E))), $path)
$gfx.DrawPath((New-Object System.Drawing.Pen($border, 1)), $path)
$gfx.DrawImage($img, (New-Object System.Drawing.Rectangle($ix, $iy, $imgW, $imgH)))
$img.Dispose()

$gfx.Dispose()
$out = Join-Path $repo 'docs\social.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host ("wrote {0} ({1} bytes), image at x={2} w={3}" -f $out, (Get-Item $out).Length, $ix, $imgW)
