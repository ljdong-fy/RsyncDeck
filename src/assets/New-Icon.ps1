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

function Draw-Arrow([Graphics]$canvas, [string]$color, [bool]$reverse) {
    $state = $canvas.Save()
    if ($reverse) { $canvas.TranslateTransform(256, 256); $canvas.RotateTransform(180) }
    $pen = [Pen]::new((Get-Color $color), 16)
    $pen.StartCap = $pen.EndCap = [LineCap]::Round
    $pen.LineJoin = [LineJoin]::Round
    $arrow = [GraphicsPath]::new()
    $arrow.AddBezier(62, 110, 78, 65, 132, 52, 168, 80)
    $arrow.AddLine(168, 80, 192, 104)
    $head = [PointF[]]@([PointF]::new(154, 104), [PointF]::new(192, 104), [PointF]::new(192, 66))
    $canvas.DrawPath($pen, $arrow)
    $canvas.DrawLines($pen, $head)
    $canvas.Restore($state)
    $arrow.Dispose()
    $pen.Dispose()
}

function New-Frame([int]$size) {
    $image = [Bitmap]::new($size, $size, [PixelFormat]::Format32bppArgb)
    $g = [Graphics]::FromImage($image)
    $g.SmoothingMode = [SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $shadow = New-RoundedPath 6 11 244 60
    $tile = New-RoundedPath 6 5 244 60
    $edge = New-RoundedPath 9 8 238 57
    $shadowBrush = [SolidBrush]::new([Color]::FromArgb(125, 4, 12, 35))
    $tileBrush = [LinearGradientBrush]::new([PointF]::new(34, 20), [PointF]::new(216, 242), (Get-Color '#203F8C'), (Get-Color '#0C173D'))
    $edgePen = [Pen]::new([Color]::FromArgb(95, 142, 187, 255), 3)
    $g.FillPath($shadowBrush, $shadow)
    $g.FillPath($tileBrush, $tile)
    if ($size -ge 32) { $g.DrawPath($edgePen, $edge) }
    Draw-Arrow $g '#F2F8FF' $false
    Draw-Arrow $g '#56F2D1' $true
    foreach ($resource in @($edgePen, $tileBrush, $shadowBrush, $edge, $tile, $shadow, $g)) { $resource.Dispose() }
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
