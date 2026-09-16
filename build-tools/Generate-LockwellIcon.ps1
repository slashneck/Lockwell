# Generates Lockwell\Assets\lockwell.ico and the 512x512 source PNG it is built from.
#
# The mark is a brand-gradient tile with the padlock knocked out of it, rather than a
# pale lock floating on a dark square. The old version was a thin teal lock on near-black
# with a hairline border: at 16px the lock disappeared into the background, and at poster
# size the hairline and the soft inner glow both looked cheap. A solid shape with a
# cut-out reads at every size, which is the only thing an icon has to do.
#
# Geometry is expressed as fractions of the canvas so every size is the same drawing
# rather than a resample of one.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $root 'Lockwell\Assets'
$sourcePng = Join-Path $assetsDir 'lockwell-icon-source.png'
$outputIco = Join-Path $assetsDir 'lockwell.ico'

New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
Add-Type -AssemblyName System.Drawing

function Get-RoundedRect([System.Drawing.RectangleF]$rect, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    if ($d -le 0) {
        $path.AddRectangle($rect)
        $path.CloseFigure()
        return $path
    }
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-LockwellBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size

    # --- the tile -----------------------------------------------------------
    # Nearly full bleed. Windows draws its own padding around an app icon, and a tile
    # that is inset as well ends up looking small in the taskbar.
    $pad = $s * 0.035
    $tileRect = New-Object System.Drawing.RectangleF $pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad)
    $tileRadius = $s * 0.225
    $tilePath = Get-RoundedRect $tileRect $tileRadius

    # The app's own brand gradient, teal through sky to indigo.
    $tileBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        [System.Drawing.PointF]::new($tileRect.Left, $tileRect.Top),
        [System.Drawing.PointF]::new($tileRect.Right, $tileRect.Bottom),
        [System.Drawing.Color]::FromArgb(255, 45, 212, 191),
        [System.Drawing.Color]::FromArgb(255, 139, 92, 246)
    )
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @(
        [System.Drawing.Color]::FromArgb(255, 45, 212, 191),
        [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(255, 139, 92, 246)
    )
    $blend.Positions = @(0.0, 0.5, 1.0)
    $tileBrush.InterpolationColors = $blend
    $g.FillPath($tileBrush, $tilePath)
    $tileBrush.Dispose()

    # A light pass across the top third so the tile reads as a surface, not a swatch.
    $g.SetClip($tilePath)
    $sheenRect = New-Object System.Drawing.RectangleF $tileRect.X, $tileRect.Y, $tileRect.Width, ($tileRect.Height * 0.55)
    $sheen = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        [System.Drawing.PointF]::new($sheenRect.Left, $sheenRect.Top),
        [System.Drawing.PointF]::new($sheenRect.Left, $sheenRect.Bottom),
        [System.Drawing.Color]::FromArgb(34, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255)
    )
    $g.FillRectangle($sheen, $sheenRect)
    $sheen.Dispose()
    $g.ResetClip()

    # --- the lock, cut out of the tile --------------------------------------
    # One flat ink colour, the app's darkest background, so the shape stays crisp at
    # small sizes instead of turning into a gradient smudge.
    $ink = [System.Drawing.Color]::FromArgb(255, 6, 8, 13)
    $inkBrush = New-Object System.Drawing.SolidBrush $ink

    # Shackle: one continuous path, leg up, over the top, leg down. Drawn as separate
    # arc and line segments it picks up a visible nick at each shoulder where the two
    # strokes meet, which is obvious once the icon is bigger than a taskbar button.
    $shackleStroke = $s * 0.062
    $arcRect = New-Object System.Drawing.RectangleF ($s * 0.378), ($s * 0.254), ($s * 0.244), ($s * 0.244)
    $legTop = $arcRect.Y + ($arcRect.Height / 2)
    $legBottom = $s * 0.500

    $shacklePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shacklePath.AddLine($arcRect.Left, $legBottom, $arcRect.Left, $legTop)
    $shacklePath.AddArc($arcRect.X, $arcRect.Y, $arcRect.Width, $arcRect.Height, 180, 180)
    $shacklePath.AddLine($arcRect.Right, $legTop, $arcRect.Right, $legBottom)

    $shacklePen = New-Object System.Drawing.Pen $ink, $shackleStroke
    $shacklePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $shacklePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $shacklePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($shacklePen, $shacklePath)
    $shacklePen.Dispose()
    $shacklePath.Dispose()

    # Body.
    $bodyRect = New-Object System.Drawing.RectangleF ($s * 0.308), ($s * 0.470), ($s * 0.384), ($s * 0.268)
    $bodyPath = Get-RoundedRect $bodyRect ($s * 0.072)
    $g.FillPath($inkBrush, $bodyPath)
    $inkBrush.Dispose()

    # --- the keyhole, cut back through to the gradient ----------------------
    # Painted with a brush built over the same tile rect, so the colour under the
    # keyhole is exactly the colour the tile would have had there.
    $keyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        [System.Drawing.PointF]::new($tileRect.Left, $tileRect.Top),
        [System.Drawing.PointF]::new($tileRect.Right, $tileRect.Bottom),
        [System.Drawing.Color]::FromArgb(255, 45, 212, 191),
        [System.Drawing.Color]::FromArgb(255, 139, 92, 246)
    )
    $keyBrush.InterpolationColors = $blend

    $holeR = $s * 0.040
    $holeCx = $s * 0.5
    $holeCy = $s * 0.562
    $g.FillEllipse($keyBrush, ($holeCx - $holeR), ($holeCy - $holeR), ($holeR * 2), ($holeR * 2))

    $slotW = $s * 0.042
    $slotRect = New-Object System.Drawing.RectangleF ($holeCx - $slotW / 2), $holeCy, $slotW, ($s * 0.100)
    $slotPath = Get-RoundedRect $slotRect ($slotW / 2)
    $g.FillPath($keyBrush, $slotPath)
    $keyBrush.Dispose()
    $slotPath.Dispose()

    $tilePath.Dispose()
    $bodyPath.Dispose()
    $g.Dispose()
    return $bmp
}

# Render master source at 512x512 (square)
$master = New-LockwellBitmap 512
$master.Save($sourcePng, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Source PNG: $sourcePng (512x512)"

# Build multi-size ICO
$sizes = @(256, 128, 48, 32, 16)
$ms = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($ms)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]$sizes.Count)

$imageData = New-Object System.Collections.Generic.List[byte[]]
$offset = 6 + (16 * $sizes.Count)

foreach ($s in $sizes) {
    $frame = New-LockwellBitmap $s
    $pngMs = New-Object System.IO.MemoryStream
    $frame.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $data = $pngMs.ToArray()
    $imageData.Add($data)
    $frame.Dispose()
    $pngMs.Dispose()

    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $writer.Write($dim)
    $writer.Write($dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$data.Length)
    $writer.Write([UInt32]$offset)
    $offset += $data.Length
}

foreach ($data in $imageData) { $writer.Write($data) }
[System.IO.File]::WriteAllBytes($outputIco, $ms.ToArray())
$writer.Close()
$ms.Close()
$master.Dispose()

Write-Host "Icon written: $outputIco (sizes: $($sizes -join ', '))"
