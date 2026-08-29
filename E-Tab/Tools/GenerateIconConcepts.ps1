param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\..\artifacts\icon-concepts')
)

# Renders icon style concepts as preview sheets + raw 256px PNGs so a style
# can be chosen before it is wired into the real ICO pipeline.
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

function Fill-RoundedRect($g, [int]$x, [int]$y, [int]$w, [int]$h, [int]$r, $brush)
{
    $path = New-RoundedRectPath $x $y $w $h $r
    $g.FillPath($brush, $path)
    $path.Dispose()
}

function Fill-Ellipse($g, [int]$x, [int]$y, [int]$w, [int]$h, $brush)
{
    $g.FillEllipse($brush, $x, $y, $w, $h)
}

function New-FolderPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$tabW, [int]$tabH, [int]$r)
{
    $path = New-RoundedRectPath $x $y $w $h $r
    $tab = New-RoundedRectPath $x ($y - $tabH) $tabW $tabH 8
    $path.AddPath($tab, $false)
    $tab.Dispose()
    return $path
}

function Draw-RoundedRect($g, [int]$x, [int]$y, [int]$w, [int]$h, [int]$r, $pen)
{
    $path = New-RoundedRectPath $x $y $w $h $r
    $g.DrawPath($pen, $path)
    $path.Dispose()
}

function New-StyleBitmap([string]$Style, [int]$Size)
{
    $scale = $Size / 256.0
    $bmp = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    function S([double]$value) { return [int][Math]::Round($value * $scale) }

    switch ($Style)
    {
        'FluentFolder'
        {
            # Refined current concept: gradient tile + white folder + tab strip + green dot.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 90, 200, 255),
                [System.Drawing.Color]::FromArgb(255, 0, 120, 212),
                60)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            Fill-RoundedRect $g (S 14) (S 14) (S 228) (S 104) (S 58) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(18, 255, 255, 255)))

            # Tab strip.
            Fill-RoundedRect $g (S 62) (S 54) (S 52) (S 42) (S 10) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255)))
            Fill-RoundedRect $g (S 118) (S 54) (S 76) (S 42) (S 10) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 120, 212)))

            # Folder.
            Fill-RoundedRect $g (S 46) (S 86) (S 164) (S 108) (S 22) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 255, 255, 255)))
            Fill-RoundedRect $g (S 50) (S 104) (S 156) (S 88) (S 20) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)))

            # Green status dot on the active tab.
            if ($Size -ge 32)
            {
                Fill-Ellipse $g (S 172) (S 66) (S 12) (S 12) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'FlatFolder'
        {
            # Minimal: solid accent tile + single white folder silhouette + green dot.
            $tile = New-RoundedRectPath (S 12) (S 12) (S 232) (S 232) (S 54)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), $tile)
            $tile.Dispose()

            $folder = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $folder.AddArc((S 42), (S 82), (S 40), (S 40), 180, 90)
            $folder.AddArc((S 166), (S 82), (S 40), (S 40), 270, 90)
            $folder.AddLine((S 186), (S 102), (S 186), (S 172))
            $folder.AddArc((S 166), (S 172), (S 40), (S 40), 0, 90)
            $folder.AddArc((S 42), (S 172), (S 40), (S 40), 90, 90)
            $folder.CloseFigure()
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $folder)
            $folder.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 180) (S 168) (S 18) (S 18) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'TabStack'
        {
            # Abstract: three overlapping tab cards fanning up, blue gradient.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 38, 110, 205),
                [System.Drawing.Color]::FromArgb(255, 10, 50, 120),
                35)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            Fill-RoundedRect $g (S 58) (S 96) (S 150) (S 78) (S 16) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(120, 255, 255, 255)))
            Fill-RoundedRect $g (S 48) (S 82) (S 150) (S 78) (S 16) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(170, 255, 255, 255)))
            Fill-RoundedRect $g (S 38) (S 68) (S 150) (S 78) (S 16) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)))

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 44) (S 104) (S 14) (S 14) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'DarkNeon'
        {
            # Dark tile + glowing cyan folder outline + green dot.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 62, 62, 68),
                [System.Drawing.Color]::FromArgb(255, 26, 26, 30),
                45)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            $folder = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $folder.AddArc((S 54), (S 96), (S 40), (S 40), 180, 90)
            $folder.AddArc((S 170), (S 96), (S 40), (S 40), 270, 90)
            $folder.AddLine((S 190), (S 116), (S 190), (S 178))
            $folder.AddArc((S 170), (S 178), (S 40), (S 40), 0, 90)
            $folder.AddArc((S 54), (S 178), (S 40), (S 40), 90, 90)
            $folder.CloseFigure()

            $inner = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $inner.AddArc((S 64), (S 108), (S 32), (S 32), 180, 90)
            $inner.AddArc((S 158), (S 108), (S 32), (S 32), 270, 90)
            $inner.AddLine((S 178), (S 124), (S 178), (S 168))
            $inner.AddArc((S 158), (S 168), (S 32), (S 32), 0, 90)
            $inner.AddArc((S 64), (S 168), (S 32), (S 32), 90, 90)
            $inner.CloseFigure()

            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 92, 215, 255), [Math]::Max(2, (S 7)))
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $g.DrawPath($pen, $folder)
            $g.DrawPath($pen, $inner)
            $folder.Dispose()
            $inner.Dispose()
            $pen.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 182) (S 168) (S 18) (S 18) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'SoftOrb'
        {
            # Soft radial-gradient orb with a white folder cutout.
            $tile = New-RoundedRectPath (S 18) (S 18) (S 220) (S 220) (S 66)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 116, 216, 255),
                [System.Drawing.Color]::FromArgb(255, 0, 94, 184),
                120)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            # Soft top glow.
            $glow = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(40, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
                90)
            Fill-RoundedRect $g (S 18) (S 18) (S 220) (S 110) (S 66) $glow
            $glow.Dispose()

            # Folder with a tab.
            Fill-RoundedRect $g (S 58) (S 90) (S 48) (S 34) (S 12) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 255, 255, 255)))
            Fill-RoundedRect $g (S 46) (S 102) (S 164) (S 96) (S 22) `
                ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)))

            if ($Size -ge 32)
            {
                Fill-Ellipse $g (S 176) (S 122) (S 14) (S 14) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'OrbFolder'
        {
            # Circular gradient disc + white folder.
            $tile = New-RoundedRectPath (S 10) (S 10) (S 236) (S 236) (S 118)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 90, 200, 255),
                [System.Drawing.Color]::FromArgb(255, 0, 103, 192),
                55)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            $glow = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(32, 255, 255, 255),
                [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
                90)
            Fill-RoundedRect $g (S 10) (S 10) (S 236) (S 110) (S 118) $glow
            $glow.Dispose()

            $folder = New-FolderPath (S 46) (S 100) (S 164) (S 92) (S 62) (S 22) (S 20)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $folder)
            $folder.Dispose()

            if ($Size -ge 32)
            {
                Fill-Ellipse $g (S 172) (S 158) (S 16) (S 16) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'MergeFolders'
        {
            # Two overlapping folders (merge into tabs), no tile.
            $back = New-FolderPath (S 62) (S 88) (S 164) (S 92) (S 62) (S 22) (S 20)
            $g.FillPath(
                [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(235, 0, 120, 212)),
                $back)
            $back.Dispose()

            $front = New-FolderPath (S 46) (S 108) (S 164) (S 92) (S 62) (S 22) (S 20)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $front)
            $front.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 170) (S 166) (S 18) (S 18) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'LineFolder'
        {
            # Minimal line style: solid blue tile + white folder outline.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 56)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 103, 192)), $tile)
            $tile.Dispose()

            $folder = New-FolderPath (S 48) (S 104) (S 160) (S 90) (S 60) (S 22) (S 20)
            $g.FillPath(
                [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(26, 255, 255, 255)),
                $folder)
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255), [Math]::Max(2, (S 7)))
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $g.DrawPath($pen, $folder)
            $folder.Dispose()
            $pen.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 176) (S 162) (S 18) (S 18) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'SplitTone'
        {
            # Two-tone tile with a diagonal split + white folder.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 61, 128)), $tile)

            $split = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $clip = [System.Drawing.Drawing2D.GraphicsPath]::new()
            $clip.AddPolygon(@(
                [System.Drawing.Point]::new((S 14), (S 14)),
                [System.Drawing.Point]::new((S 242), (S 14)),
                [System.Drawing.Point]::new((S 242), (S 118)),
                [System.Drawing.Point]::new((S 14), (S 182))))
            $clip.CloseFigure()
            $g.SetClip($clip, [System.Drawing.Drawing2D.CombineMode]::Intersect)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 76, 194, 255)), $split)
            $g.ResetClip()
            $split.Dispose()
            $clip.Dispose()
            $tile.Dispose()

            $folder = New-FolderPath (S 46) (S 102) (S 164) (S 92) (S 62) (S 22) (S 20)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $folder)
            $folder.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 172) (S 160) (S 18) (S 18) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'Aurora'
        {
            # Purple -> cyan gradient tile + white folder.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 139, 92, 246),
                [System.Drawing.Color]::FromArgb(255, 0, 194, 255),
                60)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            $folder = New-FolderPath (S 46) (S 100) (S 164) (S 92) (S 62) (S 22) (S 20)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $folder)
            $folder.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 172) (S 158) (S 16) (S 16) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 52, 199, 89)))
            }
        }

        'TealFold'
        {
            # Teal -> green gradient tile + white folder.
            $tile = New-RoundedRectPath (S 14) (S 14) (S 228) (S 228) (S 58)
            $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                [System.Drawing.Rectangle]::new(0, 0, $Size, $Size),
                [System.Drawing.Color]::FromArgb(255, 0, 210, 184),
                [System.Drawing.Color]::FromArgb(255, 0, 137, 123),
                60)
            $g.FillPath($grad, $tile)
            $tile.Dispose()
            $grad.Dispose()

            $folder = New-FolderPath (S 46) (S 100) (S 164) (S 92) (S 62) (S 22) (S 20)
            $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), $folder)
            $folder.Dispose()

            if ($Size -ge 24)
            {
                Fill-Ellipse $g (S 172) (S 158) (S 16) (S 16) `
                    ([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255)))
            }
        }
    }

    $g.Dispose()
    return $bmp
}

function New-PreviewSheet([string]$Style)
{
    $width = 512
    $height = 560
    $bmp = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Split background for the large preview: light left, dark right.
    $g.Clear([System.Drawing.Color]::FromArgb(255, 244, 244, 244))
    $g.FillRectangle([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 31, 31, 31)),
        $width / 2, 0, $width / 2, 310)

    $large = New-StyleBitmap $Style 256
    $g.DrawImage($large, 128, 27, 256, 256)
    $large.Dispose()

    # Small-size rows on light and dark strips.
    $sizes = @(48, 32, 24, 16)
    $rows = @(@(330, [System.Drawing.Color]::FromArgb(255, 244, 244, 244)),
              @(430, [System.Drawing.Color]::FromArgb(255, 31, 31, 31)))
    foreach ($row in $rows)
    {
        $y = $row[0]
        $bg = $row[1]
        $g.FillRectangle([System.Drawing.SolidBrush]::new($bg), 0, $y, $width, 100)

        $x = 96
        foreach ($size in $sizes)
        {
            $small = New-StyleBitmap $Style $size
            $g.DrawImage($small, $x, $y + (48 - $size) / 2, $size, $size)
            $small.Dispose()
            $x += 80
        }
    }

    # Label.
    $font = [System.Drawing.Font]::new('Segoe UI Semibold', 16)
    $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 60, 60, 60))
    $g.DrawString($Style, $font, $labelBrush, 20, 8)
    $font.Dispose()
    $labelBrush.Dispose()
    $g.Dispose()
    return $bmp
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$styles = @(
    'FluentFolder', 'FlatFolder', 'TabStack', 'DarkNeon', 'SoftOrb',
    'OrbFolder', 'MergeFolders', 'LineFolder', 'SplitTone', 'Aurora', 'TealFold')
foreach ($style in $styles)
{
    $png = New-StyleBitmap $style 256
    $png.Save((Join-Path $OutputDir "icon-$style-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $png.Dispose()

    $sheet = New-PreviewSheet $style
    $sheet.Save((Join-Path $OutputDir "preview-$style.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()

    Write-Output "WROTE $style"
}

Write-Output "OUTPUT_DIR $OutputDir"
