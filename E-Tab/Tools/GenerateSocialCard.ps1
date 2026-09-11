param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\..\docs\og-image.png')
)

Add-Type -AssemblyName System.Drawing

$W = 1200.0
$H = 630.0

function New-RoundedRectPath([double]$x, [double]$y, [double]$w, [double]$h, [double]$r)
{
    $maxR = [Math]::Min([double]($w / 2), [double]($h / 2))
    $r = [Math]::Max(1.0, [Math]::Min($r, $maxR))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
    $path.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
    $path.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
    $path.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
    $path.CloseFigure()
    return $path
}

# Draw the CJ tabs icon, auto-centred at (cx,cy) with diagonal size = size px.
function Draw-Tabs([System.Drawing.Graphics]$g, [double]$cx, [double]$cy, [double]$size)
{
    $scale = $size / 256.0
    $tab = 196.0; $dx = 40.0; $dy = -40.0; $r = 21.0
    $minX = [Math]::Min(0.0, $dx); $minY = [Math]::Min(0.0, $dy)
    $maxX = [Math]::Max($tab, $dx + $tab); $maxY = [Math]::Max($tab, $dy + $tab)
    $tx = $cx - (($minX + $maxX) / 2.0) * $scale
    $ty = $cy - (($minY + $maxY) / 2.0) * $scale

    $back = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $front = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    $pathBack = New-RoundedRectPath ($tx + $dx * $scale) ($ty + $dy * $scale) ($tab * $scale) ($tab * $scale) ($r * $scale)
    $g.FillPath($back, $pathBack); $pathBack.Dispose()

    $pathFront = New-RoundedRectPath $tx $ty ($tab * $scale) ($tab * $scale) ($r * $scale)
    $g.FillPath($front, $pathFront); $pathFront.Dispose()

    $back.Dispose(); $front.Dispose()
}

$bmp = [System.Drawing.Bitmap]::new($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias

# Soft diagonal background: light blue -> white.
$bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(0, 0),
    [System.Drawing.PointF]::new($W, $H),
    [System.Drawing.Color]::FromArgb(255, 224, 236, 255),
    [System.Drawing.Color]::FromArgb(255, 255, 255, 255))
$g.FillRectangle($bg, 0, 0, $W, $H)
$bg.Dispose()

# Icon (left), ~300px, centred around (310,315)
Draw-Tabs $g 310 315 300.0

# Text block (right)
$fmtC = [System.Drawing.StringFormat]::new()
$fmtC.Alignment = [System.Drawing.StringAlignment]::Near
$fmtC.LineAlignment = [System.Drawing.StringAlignment]::Center

$titleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 11, 78, 140))
$subBrush   = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 74, 85, 104))
$urlBrush   = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 122, 140, 160))

$titleFont = [System.Drawing.Font]::new("Segoe UI", 96, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$subFont   = [System.Drawing.Font]::new("Segoe UI", 40, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$urlFont   = [System.Drawing.Font]::new("Segoe UI", 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

$titleRect = [System.Drawing.RectangleF]::new(540, 190, 640, 130)
$g.DrawString("E-Tab", $titleFont, $titleBrush, $titleRect, $fmtC)

$subRect = [System.Drawing.RectangleF]::new(540, 315, 640, 70)
$g.DrawString("Open folders in new tabs", $subFont, $subBrush, $subRect, $fmtC)

$urlRect = [System.Drawing.RectangleF]::new(540, 400, 640, 50)
$g.DrawString("github.com/Adstrax/E-Tab", $urlFont, $urlBrush, $urlRect, $fmtC)

$titleFont.Dispose(); $subFont.Dispose(); $urlFont.Dispose()
$titleBrush.Dispose(); $subBrush.Dispose(); $urlBrush.Dispose()
$fmtC.Dispose()
$g.Dispose()

$outDir = Split-Path -Parent $OutputPath
if (!(Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "SOCIAL_CARD_WRITTEN $OutputPath"
