using namespace System.Drawing
using namespace System.Drawing.Drawing2D
using namespace System.Drawing.Imaging
param([string]$OutputDirectory = $PSScriptRoot)

Add-Type -AssemblyName System.Drawing

function Get-Color([string]$hex) { [ColorTranslator]::FromHtml($hex) }
function New-RoundedPath([float]$x, [float]$y, [float]$size, [float]$radius) {
    $path = [GraphicsPath]::new()
    $d = 2 * $radius
    $right = $x + $size - $d
    $bottom = $y + $size - $d
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($right, $y, $d, $d, 270, 90)
    $path.AddArc($right, $bottom, $d, $d, 0, 90)
    $path.AddArc($x, $bottom, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-Frame([int]$size) {
    $image = [Bitmap]::new($size, $size, [PixelFormat]::Format32bppArgb)
    $g = [Graphics]::FromImage($image)
    $g.SmoothingMode = [SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $tile = New-RoundedPath 7 7 242 55
    $background = [LinearGradientBrush]::new([PointF]::new(18, 22), [PointF]::new(238, 234), (Get-Color '#126C69'), (Get-Color '#102A43'))
    $g.FillPath($background, $tile)

    $folder = [GraphicsPath]::new()
    $folder.AddLine(48, 93, 48, 80)
    $folder.AddBezier(48, 80, 48, 66, 48, 66, 63, 66)
    $folder.AddLine(63, 66, 107, 66)
    $folder.AddLine(107, 66, 128, 85)
    $folder.AddLine(128, 85, 190, 85)
    $folder.AddBezier(190, 85, 206, 85, 206, 85, 206, 101)
    $folder.AddLine(206, 101, 206, 174)
    $folder.AddBezier(206, 174, 206, 190, 206, 190, 190, 190)
    $folder.AddLine(190, 190, 64, 190)
    $folder.AddBezier(64, 190, 48, 190, 48, 190, 48, 174)
    $folder.CloseFigure()
    $paper = [SolidBrush]::new((Get-Color '#F2FFF9'))
    $g.FillPath($paper, $folder)
    $stroke = [Pen]::new((Get-Color '#13736A'), 15)
    $stroke.StartCap = $stroke.EndCap = [LineCap]::Round
    $stroke.LineJoin = [LineJoin]::Round
    $g.DrawLines($stroke, [PointF[]]@([PointF]::new(91,130), [PointF]::new(160,130), [PointF]::new(145,115)))
    $g.DrawLines($stroke, [PointF[]]@([PointF]::new(165,158), [PointF]::new(96,158), [PointF]::new(111,173)))
    foreach ($resource in @($stroke, $paper, $folder, $background, $tile, $g)) { $resource.Dispose() }
    $image
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $frame = New-Frame $size
    $bytes = [System.IO.MemoryStream]::new()
    $frame.Save($bytes, [ImageFormat]::Png)
    if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'RsyncDeck.png'), $bytes.ToArray()) }
    [pscustomobject]@{ Size = $size; Bytes = $bytes.ToArray() }
    $bytes.Dispose()
    $frame.Dispose()
}

$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $OutputDirectory 'RsyncDeck.ico')))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = [byte]($frame.Size % 256)
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose() }
