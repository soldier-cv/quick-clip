Add-Type -AssemblyName System.Drawing

$outDir = $PSScriptRoot

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Canvas {
    $bmp = [System.Drawing.Bitmap]::new(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    return $bmp, $g
}

function Save-Png($bmp, $g, [string]$name) {
    $bmp.Save((Join-Path $outDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "saved: $name"
}

function New-GeoPen([System.Drawing.Color]$c, [float]$w) {
    $pen = [System.Drawing.Pen]::new($c, $w)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Bevel
    return $pen
}

$WHITE = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

function New-Rhombus([float]$cx, [float]$cy, [float]$w, [float]$h) {
    return [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($cx, $cy - $h / 2),
        [System.Drawing.PointF]::new($cx + $w / 2, $cy),
        [System.Drawing.PointF]::new($cx, $cy + $h / 2),
        [System.Drawing.PointF]::new($cx - $w / 2, $cy))
}

# ---------- C1：等距立方（线框几何） ----------
function New-C1 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 46

    $top = [System.Drawing.PointF[]](New-Rhombus 512 352 360 204)
    $left = [System.Drawing.PointF[]](New-Rhombus 422 515 180 322)
    $right = [System.Drawing.PointF[]](New-Rhombus 602 515 180 322)
    $g.DrawPolygon($pen, $top)
    $g.DrawPolygon($pen, $left)
    $g.DrawPolygon($pen, $right)

    Save-Png $bmp $g "concept-c1-cube.png"
}

# ---------- C2：几何 Q（圆环 + 直尾 + 实心点） ----------
function New-C2 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 56

    $g.DrawEllipse($pen, 200, 180, 560, 560)
    $g.DrawLine($pen, 668, 656, 750, 738)
    $g.FillEllipse([System.Drawing.SolidBrush]::new($WHITE), 772, 772, 64, 64)

    Save-Png $bmp $g "concept-c2-geo-q.png"
}

# ---------- C3：复制双菱（实心重叠，左上露角） ----------
function New-C3 {
    $bmp, $g = New-Canvas

    $g.FillPolygon([System.Drawing.SolidBrush]::new($WHITE), (New-Rhombus 442 340 360 204))
    $g.FillPolygon([System.Drawing.SolidBrush]::new($WHITE), (New-Rhombus 512 430 360 204))

    Save-Png $bmp $g "concept-c3-dual-gem.png"
}

# ---------- C4：阶梯列表（实心圆角条，错位下移） ----------
function New-C4 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 300 350 360 96 22))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 364 462 360 96 22))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 428 574 360 96 22))

    Save-Png $bmp $g "concept-c4-stairs.png"
}

# ---------- C5：四格弹出（3 格就位 + 1 格跳出） ----------
function New-C5 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 248 248 220 220 44))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 516 248 220 220 44))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 248 516 220 220 44))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 560 470 220 220 44))

    Save-Png $bmp $g "concept-c5-grid-pop.png"
}

# ---------- C6：双环链接（两圆环错位重叠） ----------
function New-C6 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 54

    $g.DrawEllipse($pen, 210, 210, 440, 440)
    $g.DrawEllipse($pen, 396, 396, 440, 440)

    Save-Png $bmp $g "concept-c6-rings.png"
}

# ---------- 总览 ----------
function New-PreviewAll {
    $tile = 520; $gap = 40; $margin = 48
    $width = $margin * 2 + $tile * 6 + $gap * 5
    $height = 1380
    $bmp = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::FromArgb(255, 11, 15, 26))

    $titleFont = [System.Drawing.Font]::new("Segoe UI", 56, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $titleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $titleRect = [System.Drawing.RectangleF]::new(0, 40, $width, 90)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString("QuickClip - 白色几何创意图形 · 透明背景", $titleFont, $titleBrush, $titleRect, $format)

    $labelFont = [System.Drawing.Font]::new("Segoe UI", 36, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240))
    $labels = @("C1 等距立方", "C2 几何 Q", "C3 复制双菱", "C4 阶梯列表", "C5 四格弹出", "C6 双环链接")
    $names = @("concept-c1-cube.png", "concept-c2-geo-q.png", "concept-c3-dual-gem.png", "concept-c4-stairs.png", "concept-c5-grid-pop.png", "concept-c6-rings.png")
    $bgs = @([System.Drawing.Color]::FromArgb(255, 30, 41, 59), [System.Drawing.Color]::FromArgb(255, 79, 70, 229))

    for ($row = 0; $row -lt 2; $row++) {
        for ($i = 0; $i -lt 6; $i++) {
            $x = $margin + $i * ($tile + $gap)
            $y = 180 + $row * ($tile + 140)
            $g.FillRectangle([System.Drawing.SolidBrush]::new($bgs[$row]), $x, $y, $tile, $tile)
            $src = [System.Drawing.Image]::FromFile((Join-Path $outDir $names[$i]))
            $g.DrawImage($src, $x, $y, $tile, $tile)
            $src.Dispose()
        }
    }

    $rowLabel = @("深色桌面背景", "靛蓝背景")
    for ($row = 0; $row -lt 2; $row++) {
        $rowLabelRect = [System.Drawing.RectangleF]::new($margin, 180 + $row * ($tile + 140) - 55, $width - $margin * 2, 45)
        $rowLabelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 148, 163, 184))
        $g.DrawString($rowLabel[$row], [System.Drawing.Font]::new("Segoe UI", 30, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel), $rowLabelBrush, $rowLabelRect, $format)
    }

    for ($i = 0; $i -lt 6; $i++) {
        $labelRect = [System.Drawing.RectangleF]::new($margin + $i * ($tile + $gap), 180 + 2 * ($tile + 140) + 10, $tile, 60)
        $g.DrawString($labels[$i], $labelFont, $labelBrush, $labelRect, $format)
    }

    Save-Png $bmp $g "preview-creative.png"
}

New-C1
New-C2
New-C3
New-C4
New-C5
New-C6
New-PreviewAll
Write-Host "done"
