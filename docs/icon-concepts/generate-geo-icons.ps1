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

# 几何风画笔：平头线帽 + 斜角连接，避免圆头"手绘感"
function New-GeoPen([System.Drawing.Color]$c, [float]$w) {
    $pen = [System.Drawing.Pen]::new($c, $w)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Bevel
    return $pen
}

$WHITE = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

# 通用几何剪贴板底座：直角圆角矩形 + 平头夹 + 槽线
function Draw-GeoClipboard($g, $pen, $slotPen) {
    $g.DrawPath($pen, (New-RoundedPath 272 250 480 540 28))
    $g.DrawPath($pen, (New-RoundedPath 392 170 240 140 28))
    $g.DrawLine($slotPen, 452, 252, 572, 252)
}

# ---------- G1：几何双层卡片 ----------
function New-G1 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 48

    $g.DrawPath($pen, (New-RoundedPath 240 170 500 600 24))
    $g.DrawPath($pen, (New-RoundedPath 300 230 500 600 24))

    # 实心圆角条（几何构造）
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 390 360 320 36 18))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 390 450 230 36 18))

    Save-Png $bmp $g "concept-g1-stack.png"
}

# ---------- G2：几何剪贴板 + 复制角标 ----------
function New-G2 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 48
    $slotPen = New-GeoPen $WHITE 40

    Draw-GeoClipboard $g $pen $slotPen

    $badgePen = New-GeoPen $WHITE 44
    $g.DrawPath($badgePen, (New-RoundedPath 580 616 112 112 28))
    $g.DrawPath($badgePen, (New-RoundedPath 628 664 112 112 28))

    Save-Png $bmp $g "concept-g2-copy-badge.png"
}

# ---------- G3：几何剪贴板 + 粘贴箭头（实心三角头） ----------
function New-G3 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 48
    $slotPen = New-GeoPen $WHITE 40

    Draw-GeoClipboard $g $pen $slotPen

    $g.DrawLine($pen, 512, 420, 512, 545)
    $head = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(512, 592),
        [System.Drawing.PointF]::new(452, 526),
        [System.Drawing.PointF]::new(572, 526))
    $g.FillPolygon([System.Drawing.SolidBrush]::new($WHITE), $head)

    Save-Png $bmp $g "concept-g3-paste.png"
}

# ---------- G4：几何剪贴板 + 加号（实心圆角条） ----------
function New-G4 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 48
    $slotPen = New-GeoPen $WHITE 40

    Draw-GeoClipboard $g $pen $slotPen

    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 386 496 252 48 24))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 488 394 48 252 24))

    Save-Png $bmp $g "concept-g4-plus.png"
}

# ---------- G5：几何长尾夹（夹体 + 双耳钢丝） ----------
function New-G5 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 48
    $earPen = New-GeoPen $WHITE 40

    $g.DrawPath($pen, (New-RoundedPath 352 270 320 340 24))
    $g.DrawLine($pen, 392, 350, 632, 350)
    $g.DrawPath($earPen, (New-RoundedPath 400 160 64 110 32))
    $g.DrawPath($earPen, (New-RoundedPath 560 160 64 110 32))

    Save-Png $bmp $g "concept-g5-binder-clip.png"
}

# ---------- G6：几何剪贴板 + 圆形对勾角标 ----------
function New-G6 {
    $bmp, $g = New-Canvas
    $pen = New-GeoPen $WHITE 48
    $slotPen = New-GeoPen $WHITE 40

    Draw-GeoClipboard $g $pen $slotPen

    $badge = New-GeoPen $WHITE 44
    $g.DrawEllipse($badge, 610, 628, 140, 140)
    $check = New-GeoPen $WHITE 40
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(652, 694),
        [System.Drawing.PointF]::new(680, 722),
        [System.Drawing.PointF]::new(724, 672))
    $g.DrawLines($check, $pts)

    Save-Png $bmp $g "concept-g6-check-badge.png"
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
    $g.DrawString("QuickClip - 白色几何图形 · 透明背景", $titleFont, $titleBrush, $titleRect, $format)

    $labelFont = [System.Drawing.Font]::new("Segoe UI", 36, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240))
    $labels = @("G1 双层卡片", "G2 复制角标", "G3 粘贴箭头", "G4 加号", "G5 长尾夹", "G6 对勾角标")
    $names = @("concept-g1-stack.png", "concept-g2-copy-badge.png", "concept-g3-paste.png", "concept-g4-plus.png", "concept-g5-binder-clip.png", "concept-g6-check-badge.png")
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

    Save-Png $bmp $g "preview-geo.png"
}

New-G1
New-G2
New-G3
New-G4
New-G5
New-G6
New-PreviewAll
Write-Host "done"
