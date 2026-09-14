<#
.SYNOPSIS
    Regenerates the SysPulse app icon (src/SysPulse.App/Assets/SysPulse.ico).

.DESCRIPTION
    Draws every icon size natively with GDI+ instead of downscaling one image, so small sizes
    stay crisp: 48px and up get the dial + pulse artwork, smaller sizes a simplified, heavier
    pulse line. Colors match the tokens in src/SysPulse.App/wwwroot/css/app.css.

.EXAMPLE
    ./tools/New-AppIcon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\SysPulse.App\Assets\SysPulse.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$Sizes = 16, 20, 24, 32, 40, 48, 64, 256
$Amber = '#F0A33A'

# Artwork is authored on a 256-unit grid and scaled to each size.
$Grid = 256.0

function New-Color([string]$Hex, [int]$Alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($Hex)
    [System.Drawing.Color]::FromArgb($Alpha, $c.R, $c.G, $c.B)
}

function New-Pen([string]$Hex, [double]$Width, [int]$Alpha = 255) {
    $pen = [System.Drawing.Pen]::new((New-Color $Hex $Alpha), [single]$Width)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen
}

function New-Points([double[]]$XY) {
    $points = for ($i = 0; $i -lt $XY.Count; $i += 2) { [System.Drawing.PointF]::new($XY[$i], $XY[$i + 1]) }
    , [System.Drawing.PointF[]]$points
}

function New-RoundedRect([double]$X, [double]$Y, [double]$Size, [double]$Radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $Radius * 2
    $path.AddArc([single]$X, [single]$Y, [single]$d, [single]$d, 180, 90)
    $path.AddArc([single]($X + $Size - $d), [single]$Y, [single]$d, [single]$d, 270, 90)
    $path.AddArc([single]($X + $Size - $d), [single]($Y + $Size - $d), [single]$d, [single]$d, 0, 90)
    $path.AddArc([single]$X, [single]($Y + $Size - $d), [single]$d, [single]$d, 90, 90)
    $path.CloseFigure()
    $path
}

# GDI+ has no blur, so fake the phosphor glow with wide, faint strokes under the real one.
$GlowPasses = @(
    @{ Scale = 1.8; Alpha = 22 },
    @{ Scale = 1.35; Alpha = 48 },
    @{ Scale = 1.0; Alpha = 255 }
)

function Invoke-GlowStroke([scriptblock]$Draw, [double]$Width) {
    foreach ($pass in $GlowPasses) {
        $pen = New-Pen $Amber ($Width * $pass.Scale) $pass.Alpha
        & $Draw $pen
        $pen.Dispose()
    }
}

function New-IconBitmap([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform([single]($Size / $Grid), [single]($Size / $Grid))

    $onePixel = $Grid / $Size
    $detailed = $Size -ge 48

    # Tile: warm charcoal, lighter at the top, with a one-pixel rim.
    $inset = if ($detailed) { 10 } else { 4 }
    $tile = New-RoundedRect $inset $inset ($Grid - 2 * $inset) 54
    $fill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, $Grid),
        (New-Color '#29251E'), (New-Color '#0F0E0B'))
    $g.FillPath($fill, $tile)
    $rim = [System.Drawing.Pen]::new((New-Color '#4A453B'), [single]$onePixel)
    $g.DrawPath($rim, $tile)

    if ($detailed) {
        # Dial: same geometry as the in-app gauge (starts at 6 o'clock, sweeps 270° clockwise).
        $dialRect = [System.Drawing.RectangleF]::new(50, 50, 156, 156)
        $fillFraction = 0.8
        $track = [System.Drawing.Pen]::new((New-Color '#322E26'), 16)
        $g.DrawArc($track, $dialRect, 90, 270)
        $track.Dispose()
        Invoke-GlowStroke { param($pen) $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat; $g.DrawArc($pen, $dialRect, 90, [single](270 * $fillFraction)) } 16

        if ($Size -ge 64) {
            # Scale ticks every 20%, lit up to the fill level.
            for ($i = 0; $i -le 5; $i++) {
                $fraction = $i / 5
                $angle = (90 + $fraction * 270) * [Math]::PI / 180
                $color = if ($fraction -le $fillFraction) { $Amber } else { '#5A5447' }
                $pen = New-Pen $color 5
                $g.DrawLine($pen,
                    [single](128 + 92 * [Math]::Cos($angle)), [single](128 + 92 * [Math]::Sin($angle)),
                    [single](128 + 104 * [Math]::Cos($angle)), [single](128 + 104 * [Math]::Sin($angle)))
                $pen.Dispose()
            }
        }

        $pulse = New-Points @(74, 134, 100, 134, 114, 100, 132, 164, 148, 114, 158, 134, 182, 134)
        Invoke-GlowStroke { param($pen) $g.DrawLines($pen, $pulse) } 11
    }
    else {
        # Small sizes: just a bold pulse line, which still reads at 16px.
        $pulse = New-Points @(34, 138, 80, 138, 106, 70, 144, 194, 172, 104, 188, 138, 222, 138)
        $glow = New-Pen $Amber 34 36
        $g.DrawLines($glow, $pulse)
        $glow.Dispose()
        $pen = New-Pen $Amber 26
        $g.DrawLines($pen, $pulse)
        $pen.Dispose()
    }

    $rim.Dispose()
    $fill.Dispose()
    $tile.Dispose()
    $g.Dispose()
    $bitmap
}

# Classic 32-bit DIB icon entry: BITMAPINFOHEADER, bottom-up BGRA pixels, then a 1-bpp AND mask.
function ConvertTo-IconDib([System.Drawing.Bitmap]$Bitmap) {
    $size = $Bitmap.Width
    $rect = [System.Drawing.Rectangle]::new(0, 0, $size, $size)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = [byte[]]::new($size * $size * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)   # top-down BGRA
    $Bitmap.UnlockBits($data)

    $maskRowBytes = [int]([Math]::Ceiling($size / 32.0) * 4)
    $stream = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($stream)

    $w.Write([uint32]40)                                  # biSize
    $w.Write([int32]$size)                                # biWidth
    $w.Write([int32]($size * 2))                          # biHeight (color + mask)
    $w.Write([uint16]1)                                   # biPlanes
    $w.Write([uint16]32)                                  # biBitCount
    $w.Write([uint32]0)                                   # biCompression (BI_RGB)
    $w.Write([uint32]($pixels.Length + $maskRowBytes * $size))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)

    for ($y = $size - 1; $y -ge 0; $y--) {
        $w.Write($pixels, $y * $size * 4, $size * 4)
    }

    # AND mask: 1 = transparent, for consumers that ignore the alpha channel.
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = [byte[]]::new($maskRowBytes)
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[($y * $size + $x) * 4 + 3] -eq 0) {
                $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x % 8))
            }
        }
        $w.Write($row)
    }

    $w.Flush()
    $bytes = $stream.ToArray()
    $w.Dispose()
    , $bytes
}

# Render each size, then pack them into an .ico. 256px is stored as PNG (standard for large entries);
# smaller sizes as classic DIBs, which every Windows API and System.Drawing can read.
$images = foreach ($size in $Sizes) {
    $bitmap = New-IconBitmap $size
    if ($size -ge 256) {
        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
    }
    else {
        $bytes = ConvertTo-IconDib $bitmap
    }
    $bitmap.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type: icon
$writer.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }   # 0 means 256
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)            # palette size
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # color planes
    $writer.Write([uint16]32)         # bits per pixel
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) {
    $writer.Write($image.Bytes)
}
$writer.Flush()

$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force (Split-Path $OutputPath) | Out-Null
[System.IO.File]::WriteAllBytes($OutputPath, $ico.ToArray())
$writer.Dispose()

Write-Host "Wrote $OutputPath ($($Sizes -join ', ') px)"
