param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Icon.ico')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r)
{
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-FolderPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$tabW, [int]$tabH, [int]$r)
{
    $path = New-RoundedRectPath $x $y $w $h $r
    $tab = New-RoundedRectPath $x ($y - $tabH) $tabW $tabH 8
    $path.AddPath($tab, $false)
    $tab.Dispose()
    return $path
}

function New-IconBitmap([int]$size)
{
    $scale = $size / 256.0
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    function S([double]$value) { return [int][Math]::Round($value * $scale) }

    # MergeFolders: two overlapping folders (merge into tabs) + green status dot.
    $back = New-FolderPath (S 62) (S 88) (S 164) (S 92) (S 62) (S 22) (S 20)
    $g.FillPath(
        [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 0, 120, 212)),
        $back)
    $back.Dispose()

    $front = New-FolderPath (S 46) (S 108) (S 164) (S 92) (S 62) (S 22) (S 20)
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $front)
    $front.Dispose()

    if ($size -ge 24)
    {
        $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89))
        $g.FillEllipse($dotBrush, (S 170), (S 166), (S 18), (S 18))
    }

    $g.Dispose()
    return $bmp
}

function Write-Png([string]$path, [int]$size)
{
    $bmp = New-IconBitmap $size
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Write-Ico([string]$path)
{
    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $images = @()

    foreach ($size in $sizes)
    {
        $bmp = New-IconBitmap $size
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
