param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\Icon.ico')
)

Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r)
{
    $maxR = [Math]::Min([int]($w / 2), [int]($h / 2))
    $r = [Math]::Max(1, [Math]::Min($r, $maxR))
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

    # Small sizes fill more of the canvas so the tray icon does not look tiny.
    $fillScale = if ($size -lt 48) { 1.14 } else { 1.0 }
    function S([double]$value)
    {
        return [int][Math]::Round((($value - 128) * $fillScale + 128) * $scale)
    }

    if ($size -le 32)
    {
        # Compact tray variant: filled blue tile + white folder so the icon is
        # bold and legible at 16-32px. The large app icon keeps the approved
        # two-folder MergeFolders design below.
        $tile = New-RoundedRectPath (S 10) (S 10) (S 236) (S 236) (S 58)
        $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), $tile)
        $tile.Dispose()

        $folder = New-FolderPath (S 46) (S 102) (S 164) (S 92) (S 62) (S 22) (S 20)
        $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $folder)
        $folder.Dispose()

        if ($size -ge 24)
        {
            $dotSize = if ($size -lt 64) { 22 } else { 18 }
            $dotX = 170 - (($dotSize - 18) / 2)
            $dotY = 166 - (($dotSize - 18) / 2)
            $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89))
            $g.FillEllipse($dotBrush, (S $dotX), (S $dotY), (S $dotSize), (S $dotSize))
        }
    }
    else
    {
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
            $dotSize = if ($size -lt 64) { 22 } else { 18 }
            $dotX = 170 - (($dotSize - 18) / 2)
            $dotY = 166 - (($dotSize - 18) / 2)
            $dotBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89))
            $g.FillEllipse($dotBrush, (S $dotX), (S $dotY), (S $dotSize), (S $dotSize))
        }
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
