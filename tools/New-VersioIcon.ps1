<#
.SYNOPSIS
    Draws the Versio application icon and packs it into a multi-resolution .ico.

.DESCRIPTION
    A Windows .ico is not one image; it is a container holding the same mark drawn
    at several sizes. Windows picks whichever fits the slot - 16 px in the title bar
    and Explorer's details view, 32 px in the taskbar, 256 px in the icon view - and
    it does NOT downscale gracefully. A 256 px drawing squeezed to 16 px turns into
    grey soup, which is why this script redraws the geometry per size and switches
    to a simplified variant below 32 px rather than resampling one master image.

    Encoding follows the convention Windows shells actually expect:

        <= 48 px   BMP (BITMAPINFOHEADER + BGRA + AND mask)
        >= 64 px   PNG

    PNG entries at every size do work on Windows 10 and 11, but a few older shell
    surfaces and installer UIs still read the small entries as DIBs. Writing both
    formats costs nothing at build time and removes the entire question.

    No third-party dependency: GDI+ ships with the .NET Framework that Windows
    PowerShell 5.1 runs on. The geometry mirrors assets/versio.svg - see the note
    there about keeping the two in step.

.PARAMETER OutputPath
    Where to write the .ico. Defaults to assets/versio.ico beside this script's repo.

.PARAMETER PreviewDirectory
    If given, also writes each size as a standalone .png for eyeballing. Use it.
    A 16 px entry that is unreadable is the failure mode this script exists to
    prevent, and the only way to know is to look at it.

.EXAMPLE
    pwsh tools/New-VersioIcon.ps1 -PreviewDirectory artifacts/icon-preview
#>
[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$PreviewDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# The sizes Windows asks for. 24 is not optional despite being unusual: it is the
# size the Alt-Tab switcher and some toolbars request, and if it is missing Windows
# downscales 32 to 24 with visible artefacts.
$Sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

# Design space. All geometry below is expressed in a 64x64 grid and scaled at draw
# time, so a size is added by adding a number above - never by editing coordinates.
$Design = 64.0

# Palette, lifted from src/Versio.App/Theme/tokens/. Named, not inlined, so a token
# change is a one-line edit here.
$Ink = @{
    Tile    = [System.Drawing.ColorTranslator]::FromHtml('#1D2026')  # Surface.Base
    Oldest  = [System.Drawing.ColorTranslator]::FromHtml('#5C6472')  # Text.Disabled
    Middle  = [System.Drawing.ColorTranslator]::FromHtml('#8B93A1')  # Status.Neutral
    Current = [System.Drawing.ColorTranslator]::FromHtml('#E8EAED')  # Text.Primary
    Badge   = [System.Drawing.ColorTranslator]::FromHtml('#4ADE80')  # Status.Added
}

function New-RoundedPath {
    <#
      .SYNOPSIS
        A rounded rectangle as a GraphicsPath. GDI+ has no primitive for it.
    #>
    param([double]$X, [double]$Y, [double]$W, [double]$H, [double]$R)

    # Clamp so a radius larger than the box degrades to a capsule instead of
    # producing the inverted arcs GDI+ draws for a negative sweep.
    $R = [Math]::Min($R, [Math]::Min($W, $H) / 2.0)
    $d = $R * 2.0

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($R -le 0) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF $X, $Y, $W, $H))
        return $path
    }

    $path.AddArc([float]$X,             [float]$Y,             [float]$d, [float]$d, 180, 90)
    $path.AddArc([float]($X + $W - $d), [float]$Y,             [float]$d, [float]$d, 270, 90)
    $path.AddArc([float]($X + $W - $d), [float]($Y + $H - $d), [float]$d, [float]$d,   0, 90)
    $path.AddArc([float]$X,             [float]($Y + $H - $d), [float]$d, [float]$d,  90, 90)
    $path.CloseFigure()
    return $path
}

function Add-Plate {
    param($Graphics, [double]$X, [double]$Y, [double]$W, [double]$H, [double]$R, $Color)

    $brush = New-Object System.Drawing.SolidBrush $Color
    $path = New-RoundedPath -X $X -Y $Y -W $W -H $H -R $R
    try { $Graphics.FillPath($brush, $path) } finally { $path.Dispose(); $brush.Dispose() }
}

function Add-Disc {
    param($Graphics, [double]$Cx, [double]$Cy, [double]$R, $Color)

    $brush = New-Object System.Drawing.SolidBrush $Color
    try {
        $Graphics.FillEllipse($brush, [float]($Cx - $R), [float]($Cy - $R), [float]($R * 2), [float]($R * 2))
    } finally { $brush.Dispose() }
}

function New-IconBitmap {
    <#
      .SYNOPSIS
        Draw the mark at one pixel size.
      .DESCRIPTION
        Two variants, and the threshold is deliberate.

        The 1-unit stagger between plates is half a pixel at 32 px: too small to
        read as a stagger, large enough to smear every plate edge into grey. So
        the staggered variant only runs at 48 px and above. Below that the plates
        square up, the gaps widen to a full clean pixel, and the badge grows until
        it survives - at 16 px it is the only thing distinguishing this from a
        generic list glyph.

        Small-variant coordinates are all multiples of 4, which lands every plate
        edge on a whole pixel at both 16 px and 32 px - the two sizes that matter
        most, being Explorer's list view and the taskbar. 20 and 24 fall on half
        pixels and are slightly soft; there is no geometry that snaps at every
        size, so the common ones win.
    #>
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.ScaleTransform([float]($Size / $Design), [float]($Size / $Design))

        Add-Plate -Graphics $g -X 0 -Y 0 -W 64 -H 64 -R 14 -Color $Ink.Tile

        if ($Size -ge 48) {
            Add-Plate -Graphics $g -X 15 -Y 40 -W 34 -H 8 -R 3 -Color $Ink.Oldest
            Add-Plate -Graphics $g -X 14 -Y 28 -W 34 -H 8 -R 3 -Color $Ink.Middle
            Add-Plate -Graphics $g -X 13 -Y 16 -W 34 -H 8 -R 3 -Color $Ink.Current

            # Punched in the tile colour first, so the badge reads as a notch cut out
            # of the top plate rather than a sticker dropped on it.
            Add-Disc -Graphics $g -Cx 46 -Cy 20 -R 7.5 -Color $Ink.Tile
            Add-Disc -Graphics $g -Cx 46 -Cy 20 -R 5.2 -Color $Ink.Badge
        }
        else {
            Add-Plate -Graphics $g -X 12 -Y 44 -W 28 -H 8 -R 3 -Color $Ink.Oldest
            Add-Plate -Graphics $g -X 12 -Y 28 -W 28 -H 8 -R 3 -Color $Ink.Middle
            Add-Plate -Graphics $g -X 12 -Y 12 -W 28 -H 8 -R 3 -Color $Ink.Current

            Add-Disc -Graphics $g -Cx 46.5 -Cy 20 -R 8.5 -Color $Ink.Tile
            Add-Disc -Graphics $g -Cx 46.5 -Cy 20 -R 6.5 -Color $Ink.Badge
        }
    }
    finally { $g.Dispose() }

    return $bitmap
}

function ConvertTo-PngBytes {
    param($Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    } finally { $stream.Dispose() }
}

function ConvertTo-DibBytes {
    <#
      .SYNOPSIS
        A 32-bit bottom-up DIB with the AND mask an .ico entry requires.
      .DESCRIPTION
        Three details bite here. The header height is DOUBLE the image height,
        because the structure describes the colour bitmap and the mask stacked.
        Rows run bottom-up. And the AND mask must be present and 4-byte aligned
        per row even though a 32-bit entry's alpha channel makes it redundant -
        omit it and the sizes stop matching, which some readers treat as a corrupt
        entry rather than ignoring.
    #>
    param($Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        $writer.Write([uint32]40)        # biSize
        $writer.Write([int32]$w)         # biWidth
        $writer.Write([int32]($h * 2))   # biHeight: colour + mask
        $writer.Write([uint16]1)         # biPlanes
        $writer.Write([uint16]32)        # biBitCount
        $writer.Write([uint32]0)         # biCompression: BI_RGB
        $writer.Write([uint32]($w * $h * 4))
        $writer.Write([int32]0); $writer.Write([int32]0)   # pels per metre
        $writer.Write([uint32]0); $writer.Write([uint32]0) # palette

        for ($y = $h - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $w; $x++) {
                $p = $Bitmap.GetPixel($x, $y)
                $writer.Write([byte]$p.B)
                $writer.Write([byte]$p.G)
                $writer.Write([byte]$p.R)
                $writer.Write([byte]$p.A)
            }
        }

        # AND mask: all zero (opaque). Alpha above is what actually cuts the shape.
        $maskStride = [Math]::Floor(($w + 31) / 32) * 4
        $blank = New-Object byte[] $maskStride
        for ($y = 0; $y -lt $h; $y++) { $writer.Write($blank, 0, $maskStride) }

        $writer.Flush()
        return $stream.ToArray()
    } finally { $writer.Dispose(); $stream.Dispose() }
}

# --- assemble -------------------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'assets\versio.ico' }

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir | Out-Null }

if ($PreviewDirectory -and -not (Test-Path $PreviewDirectory)) {
    New-Item -ItemType Directory -Path $PreviewDirectory | Out-Null
}

$entries = @()
foreach ($size in $Sizes) {
    $bitmap = New-IconBitmap -Size $size
    try {
        # PNG above 48, DIB at and below - see the description block.
        $payload = if ($size -ge 64) { ConvertTo-PngBytes $bitmap } else { ConvertTo-DibBytes $bitmap }

        if ($PreviewDirectory) {
            $bitmap.Save((Join-Path $PreviewDirectory "versio-$size.png"),
                         [System.Drawing.Imaging.ImageFormat]::Png)
        }

        $entries += [pscustomobject]@{
            Size    = $size
            Payload = $payload
            Format  = if ($size -ge 64) { 'PNG' } else { 'DIB' }
        }
    } finally { $bitmap.Dispose() }
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0)                # reserved
    $writer.Write([uint16]1)                # type: icon
    $writer.Write([uint16]$entries.Count)

    # Every payload sits after the directory, so offsets are known before any data
    # is written - no seek-back needed.
    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        # 256 is written as 0: the field is one byte and 256 does not fit.
        $dim = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dim)           # width
        $writer.Write([byte]$dim)           # height
        $writer.Write([byte]0)              # palette entries
        $writer.Write([byte]0)              # reserved
        $writer.Write([uint16]1)            # colour planes
        $writer.Write([uint16]32)           # bits per pixel
        $writer.Write([uint32]$entry.Payload.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Payload.Length
    }

    foreach ($entry in $entries) { $writer.Write($entry.Payload, 0, $entry.Payload.Length) }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($OutputPath, $stream.ToArray())
} finally { $writer.Dispose(); $stream.Dispose() }

$total = (Get-Item $OutputPath).Length
Write-Output "Wrote $OutputPath ($total bytes)"
foreach ($entry in $entries) {
    Write-Output ("  {0,3} px  {1}  {2,6} bytes" -f $entry.Size, $entry.Format, $entry.Payload.Length)
}
if ($PreviewDirectory) { Write-Output "Previews in $PreviewDirectory - look at the 16 px one." }
