param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Icon.ico')
)

Add-Type -AssemblyName System.Drawing

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

# C2: a deck of two stacked tabs. Mid-blue back peeks up-right, white front on
# top. Anti-aliased and auto-centred so it fills the square at any size.
function New-TabsBitmap([int]$size)
{
    $scale = $size / 256.0
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $tab = 150.0; $dx = 30.0; $dy = -30.0; $r = 16.0
    $minX = [Math]::Min(0.0, $dx); $minY = [Math]::Min(0.0, $dy)
    $maxX = [Math]::Max($tab, $dx + $tab); $maxY = [Math]::Max($tab, $dy + $tab)
    $tx = ($size / 2.0) - (($minX + $maxX) / 2.0) * $scale
    $ty = ($size / 2.0) - (($minY + $maxY) / 2.0) * $scale

    $back = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $front = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))

    $pathBack = New-RoundedRectPath ($tx + $dx * $scale) ($ty + $dy * $scale) ($tab * $scale) ($tab * $scale) ($r * $scale)
    $g.FillPath($back, $pathBack); $pathBack.Dispose()

    $pathFront = New-RoundedRectPath $tx $ty ($tab * $scale) ($tab * $scale) ($r * $scale)
    $g.FillPath($front, $pathFront); $pathFront.Dispose()

    $back.Dispose(); $front.Dispose(); $g.Dispose()
    return $bmp
}

function Write-Png([string]$path, [int]$size)
{
    $bmp = New-TabsBitmap $size
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Write-Ico([string]$path)
{
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()

    foreach ($size in $sizes)
    {
        $bmp = New-TabsBitmap $size
        $stream = [System.IO.MemoryStream]::new()
        $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , ($stream.ToArray())
        $bmp.Dispose()
        $stream.Dispose()
    }

    $file = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($file)
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    for ($i = 0; $i -lt $images.Count; $i++)
    {
        $size = $sizes[$i]
        $data = $images[$i]
        $dimension = if ($size -ge 256) { 0 } else { $size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$data.Length)
        $writer.Write([uint32]$offset)
        $offset += $data.Length
    }

    foreach ($data in $images)
    {
        $writer.Write($data)
    }

    $writer.Dispose()
    $file.Dispose()
}

Write-Ico $OutputPath
Write-Png (Join-Path $PSScriptRoot '..\Icon-32.png') 32
Write-Output "ICON_WRITTEN $OutputPath"