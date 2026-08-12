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
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    return $bmp, $g
}

function Save-Png($bmp, $g, [string]$name) {
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host "saved: $path"
}

# 企业级配色：靛蓝 / 深海军蓝 / 金 / 绿 / 浅灰，全部纯色扁平
$INDIGO   = [System.Drawing.Color]::FromArgb(255, 79, 70, 229)
$INDIGO_D = [System.Drawing.Color]::FromArgb(255, 67, 56, 202)
$NAVY     = [System.Drawing.Color]::FromArgb(255, 30, 41, 59)
$GOLD     = [System.Drawing.Color]::FromArgb(255, 245, 158, 11)
$GREEN    = [System.Drawing.Color]::FromArgb(255, 16, 185, 129)
$INK      = [System.Drawing.Color]::FromArgb(255, 51, 65, 85)
$PAPER    = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)
$WHITE    = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

# ---------- 概念 1：负空间剪贴板（靛蓝实底 + 白色镂空） ----------
function New-Concept1 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO), (New-RoundedPath 0 0 1024 1024 230))

    # 白色剪贴板
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 272 220 480 600 64))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 392 150 240 150 64))

    # 负空间镂空：夹子口 + 文字行
    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO), (New-RoundedPath 442 238 140 44 22))
    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO), (New-RoundedPath 362 470 300 26 13))
    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO), (New-RoundedPath 362 560 220 26 13))

    Save-Png $bmp $g "concept-1-negative.png"
}

# ---------- 概念 2：线稿剪贴板（浅底 + 细线稿，最企业风） ----------
function New-Concept2 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($PAPER), (New-RoundedPath 0 0 1024 1024 230))
    $g.DrawPath([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240), 6), (New-RoundedPath 0 0 1024 1024 230))

    $pen = [System.Drawing.Pen]::new($INK, 30)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $g.DrawPath($pen, (New-RoundedPath 272 230 480 600 48))
    $g.DrawPath($pen, (New-RoundedPath 392 140 240 170 60))

    $linePen = [System.Drawing.Pen]::new($INK, 20)
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($linePen, 372, 480, 652, 480)
    $g.DrawLine($linePen, 372, 560, 592, 560)

    Save-Png $bmp $g "concept-2-lineart.png"
}

# ---------- 概念 3：几何 QC（圆环 Q + 金色句点） ----------
function New-Concept3 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($PAPER), (New-RoundedPath 0 0 1024 1024 230))
    $g.DrawPath([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240), 6), (New-RoundedPath 0 0 1024 1024 230))

    # Q 圆环
    $pen = [System.Drawing.Pen]::new($INDIGO, 58)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawEllipse($pen, 190, 190, 560, 560)

    # Q 尾巴（与金色句点保持间距）
    $g.DrawLine($pen, 660, 668, 736, 744)

    # 金色句点
    $g.FillEllipse([System.Drawing.SolidBrush]::new($GOLD), 774, 780, 84, 84)

    Save-Png $bmp $g "concept-3-geo-qc.png"
}

# ---------- 概念 4：卡片层叠（无背景，透明，主体约占画布 78%） ----------
function New-Concept4 {
    $bmp, $g = New-Canvas

    # 三层卡片，平面偏移（不旋转），主体放大填充画布
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 199, 210, 254)), (New-RoundedPath 84 62 792 836 60))
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 129, 140, 248)), (New-RoundedPath 113 93 792 836 60))
    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO), (New-RoundedPath 146 124 792 836 60))

    # 顶层内容：四白条 + 金条，加长加宽、行距拉开、上下留白均匀
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 196 346 620 36 18))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 196 442 470 36 18))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 196 538 620 36 18))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 196 634 430 36 18))
    $g.FillPath([System.Drawing.SolidBrush]::new($GOLD), (New-RoundedPath 196 730 300 36 18))

    Save-Png $bmp $g "concept-4-cards.png"
}

# ---------- 概念 5：QC 字标（靛蓝实底 + 白色 Q + 金色句点） ----------
function New-Concept5 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO), (New-RoundedPath 0 0 1024 1024 230))

    # 白色 Q 圆环
    $pen = [System.Drawing.Pen]::new($WHITE, 58)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawEllipse($pen, 190, 190, 560, 560)

    # Q 尾巴
    $g.DrawLine($pen, 660, 668, 736, 744)

    # 金色句点（与尾巴留出间距）
    $g.FillEllipse([System.Drawing.SolidBrush]::new($GOLD), 774, 780, 84, 84)

    Save-Png $bmp $g "concept-5-qc-mark.png"
}

# ---------- 概念 6：对勾剪贴板（无背景，白色图形带浅描边保证浅色桌面可见） ----------
function New-Concept6 {
    $bmp, $g = New-Canvas

    # 白色剪贴板 + 灰蓝描边（透明背景下浅色桌面也清晰）
    $board = New-RoundedPath 272 240 480 580 56
    $clip = New-RoundedPath 392 170 240 140 56
    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 163, 177, 194), 18)
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), $board)
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), $clip)
    $g.DrawPath($outline, $board)
    $g.DrawPath($outline, $clip)
    $g.FillPath([System.Drawing.SolidBrush]::new($INDIGO_D), (New-RoundedPath 442 250 140 44 22))

    $check = [System.Drawing.Pen]::new($GREEN, 54)
    $check.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $check.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $check.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(430, 520),
        [System.Drawing.PointF]::new(520, 610),
        [System.Drawing.PointF]::new(650, 430))
    $g.DrawLines($check, $pts)

    Save-Png $bmp $g "concept-6-check.png"
}

# ---------- 总览图（每格上下分浅/深两半，模拟不同桌面背景） ----------
function New-PreviewAll {
    $tile = 620; $gap = 40; $margin = 48
    $width = $margin * 2 + $tile * 6 + $gap * 5
    $height = 980
    $bmp = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::FromArgb(255, 11, 15, 26))

    $titleFont = [System.Drawing.Font]::new("Segoe UI", 64, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $titleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $titleRect = [System.Drawing.RectangleF]::new(0, 40, $width, 90)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString("QuickClip Icon Concepts - 上半浅色 / 下半深色", $titleFont, $titleBrush, $titleRect, $format)

    $labelFont = [System.Drawing.Font]::new("Segoe UI", 40, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240))
    $labels = @("1 负空间剪贴板", "2 线稿剪贴板", "3 几何 QC", "4 卡片层叠", "5 QC 字标", "6 对勾剪贴板")
    $names = @("concept-1-negative.png", "concept-2-lineart.png", "concept-3-geo-qc.png", "concept-4-cards.png", "concept-5-qc-mark.png", "concept-6-check.png")

    for ($i = 0; $i -lt 6; $i++) {
        $x = $margin + $i * ($tile + $gap)
        $y = 170

        # 上半浅色 / 下半深色
        $light = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 241, 245, 249))
        $dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 30, 41, 59))
        $g.FillRectangle($light, $x, $y, $tile, $tile / 2)
        $g.FillRectangle($dark, $x, $y + $tile / 2, $tile, $tile / 2)
        $g.DrawLine([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 71, 85, 105), 4), $x, $y + $tile / 2, $x + $tile, $y + $tile / 2)

        $src = [System.Drawing.Image]::FromFile((Join-Path $outDir $names[$i]))
        $g.DrawImage($src, $x, $y, $tile, $tile)
        $src.Dispose()

        $labelRect = [System.Drawing.RectangleF]::new($x, $y + $tile + 26, $tile, 70)
        $g.DrawString($labels[$i], $labelFont, $labelBrush, $labelRect, $format)
    }

    Save-Png $bmp $g "preview-all.png"
}

New-Concept1
New-Concept2
New-Concept3
New-Concept4
New-Concept5
New-Concept6
New-PreviewAll
Write-Host "done: $outDir"
