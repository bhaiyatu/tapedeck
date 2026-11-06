# Renders the Tapedeck cassette logo to a multi-size .ico (and a 256px PNG) using System.Drawing.
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\Tapedeck\Assets"))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Cassette body with a warm gradient
    $body = New-RoundedRect (12 * $s) (44 * $s) (232 * $s) (168 * $s) (26 * $s)
    $rect = New-Object System.Drawing.RectangleF 0, (44 * $s), $size, (168 * $s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 255, 94, 98)), ([System.Drawing.Color]::FromArgb(255, 255, 153, 102)), 30
    $g.FillPath($grad, $body)

    # Label window
    $label = New-RoundedRect (40 * $s) (72 * $s) (176 * $s) (84 * $s) (16 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 28, 22, 36))), $label)

    # Reels
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 22, 36))
    foreach ($cx in @(88, 168)) {
        $r = 26 * $s
        $g.FillEllipse($white, ($cx * $s) - $r, (114 * $s) - $r, $r * 2, $r * 2)
        $r2 = 10 * $s
        $g.FillEllipse($dark, ($cx * $s) - $r2, (114 * $s) - $r2, $r2 * 2, $r2 * 2)
    }

    # Bottom trapezoid
    if ($size -ge 32) {
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF (72 * $s), (212 * $s)),
            (New-Object System.Drawing.PointF (84 * $s), (178 * $s)),
            (New-Object System.Drawing.PointF (172 * $s), (178 * $s)),
            (New-Object System.Drawing.PointF (184 * $s), (212 * $s)))
        $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(90, 28, 22, 36))), $pts)
    }
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Render $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($sz -eq 256) { $bmp.Save((Join-Path $OutDir "Tapedeck.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# Pack PNG frames into an ICO container
$fs = [System.IO.File]::Create((Join-Path $OutDir "Tapedeck.ico"))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $bw.Write($png) }
$bw.Close()
Write-Output "Wrote icon to $OutDir"

# Discord: the app icon and the small badge on the Rich Presence card. Both are uploaded in the
# Developer Portal (the badge as the art asset "tapedeck"). A solid square, since Discord rounds or circles it.
$discordDir = Join-Path $PSScriptRoot "..\assets\discord"
New-Item -ItemType Directory -Force $discordDir | Out-Null
$badgeSize = 1024
$badge = New-Object System.Drawing.Bitmap $badgeSize, $badgeSize, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bg = [System.Drawing.Graphics]::FromImage($badge)
$bg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$bg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$bg.Clear([System.Drawing.Color]::FromArgb(255, 28, 22, 36))
$cassette = Render ([int]($badgeSize * 0.8))
$inset = ($badgeSize - $cassette.Width) / 2
$bg.DrawImage($cassette, [float]$inset, [float]$inset, [float]$cassette.Width, [float]$cassette.Height)
$bg.Dispose()
$cassette.Dispose()
$badge.Save((Join-Path $discordDir "tapedeck.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$badge.Dispose()
Write-Output "Wrote Discord badge to $discordDir"
