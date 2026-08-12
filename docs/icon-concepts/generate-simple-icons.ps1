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

function New-RoundPen([System.Drawing.Color]$c, [float]$w) {
    $pen = [System.Drawing.Pen]::new($c, $w)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    return $pen
}

$WHITE = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

# ---------- W1：白色粗线剪贴板（透明背景） ----------
function New-W1 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 58

    $g.DrawPath($pen, (New-RoundedPath 262 250 500 570 60))
    $g.DrawPath($pen, (New-RoundedPath 392 160 240 160 64))
    $g.DrawLine($pen, 442, 260, 582, 260)

    $linePen = New-RoundPen $WHITE 40
    $g.DrawLine($linePen, 352, 470, 672, 470)
    $g.DrawLine($linePen, 352, 560, 572, 560)

    Save-Png $bmp $g "concept-w1-outline.png"
}

# ---------- W2：白色实心剪贴板（透明镂空横线） ----------
function New-W2 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 262 250 500 540 56))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 392 160 240 160 60))
    # 透明镂空：夹口 + 文本行
    $clip = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $clip.AddRectangle([System.Drawing.RectangleF]::new(442, 250, 140, 40))
    $clip.AddRectangle([System.Drawing.RectangleF]::new(352, 470, 300, 30))
    $clip.AddRectangle([System.Drawing.RectangleF]::new(352, 560, 220, 30))
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 0, 0)), $clip)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.FillPath([System.Drawing.Brushes]::Transparent, $clip)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver

    Save-Png $bmp $g "concept-w2-solid.png"
}

# ---------- W3：白色粗线剪贴板 + 白色对勾（全白单色） ----------
function New-W3 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 262 250 500 570 60))
    $g.DrawPath($pen, (New-RoundedPath 392 160 240 160 64))
    $g.DrawLine($pen, 442, 260, 582, 260)

    $check = New-RoundPen $WHITE 62
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(430, 510),
        [System.Drawing.PointF]::new(515, 595),
        [System.Drawing.PointF]::new(640, 430))
    $g.DrawLines($check, $pts)

    Save-Png $bmp $g "concept-w3-check.png"
}

# ---------- W4：白色实心剪贴板 + 透明对勾镂空 ----------
function New-W4 {
    $bmp, $g = New-Canvas

    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 262 250 500 540 56))
    $g.FillPath([System.Drawing.SolidBrush]::new($WHITE), (New-RoundedPath 392 160 240 160 60))

    # 透明镂空：夹口 + 对勾
    $cut = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $cut.AddRectangle([System.Drawing.RectangleF]::new(442, 250, 140, 40))
    $cut.AddLines([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(430, 510),
        [System.Drawing.PointF]::new(515, 595),
        [System.Drawing.PointF]::new(640, 430)))
    $pen = New-RoundPen ([System.Drawing.Color]::FromArgb(255, 0, 0, 0)) 62
    $g.DrawPath([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 0, 0, 0), 62), $cut)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $g.DrawPath($pen, $cut)
    $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver

    Save-Png $bmp $g "concept-w4-solid-check.png"
}

# ---------- 总览（深色底 + 靛蓝底两行，展示白色图形的可辨识度） ----------
function New-PreviewAll {
    $tile = 620; $gap = 40; $margin = 48
    $width = $margin * 2 + $tile * 4 + $gap * 3
    $height = 1450
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
    $g.DrawString("QuickClip - 纯白图形 · 透明背景（ChatGPT 风）", $titleFont, $titleBrush, $titleRect, $format)

    $labelFont = [System.Drawing.Font]::new("Segoe UI", 40, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240))
    $labels = @("W1 白线剪贴板", "W2 白实心剪贴板", "W3 白线对勾", "W4 白实心对勾")
    $names = @("concept-w1-outline.png", "concept-w2-solid.png", "concept-w3-check.png", "concept-w4-solid-check.png")
    $bgs = @([System.Drawing.Color]::FromArgb(255, 30, 41, 59), [System.Drawing.Color]::FromArgb(255, 79, 70, 229))

    for ($row = 0; $row -lt 2; $row++) {
        for ($i = 0; $i -lt 4; $i++) {
            $x = $margin + $i * ($tile + $gap)
            $y = 170 + $row * ($tile + 150)

            $g.FillRectangle([System.Drawing.SolidBrush]::new($bgs[$row]), $x, $y, $tile, $tile)
            $src = [System.Drawing.Image]::FromFile((Join-Path $outDir $names[$i]))
            $g.DrawImage($src, $x, $y, $tile, $tile)
            $src.Dispose()
        }
    }

    $rowLabel = @("深色桌面背景", "靛蓝背景")
    for ($row = 0; $row -lt 2; $row++) {
        $rowLabelRect = [System.Drawing.RectangleF]::new($margin, 170 + $row * ($tile + 150) - 60, $width - $margin * 2, 50)
        $rowLabelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 148, 163, 184))
        $g.DrawString($rowLabel[$row], [System.Drawing.Font]::new("Segoe UI", 32, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel), $rowLabelBrush, $rowLabelRect, $format)
    }

    for ($i = 0; $i -lt 4; $i++) {
        $labelRect = [System.Drawing.RectangleF]::new($margin + $i * ($tile + $gap), 170 + 2 * ($tile + 150) + 10, $tile, 70)
        $g.DrawString($labels[$i], $labelFont, $labelBrush, $labelRect, $format)
    }

    Save-Png $bmp $g "preview-simple.png"
}

# ---------- W5：双层卡片线稿（复制层叠） ----------
function New-W5 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 222 158 500 620 56))
    $g.DrawPath($pen, (New-RoundedPath 282 218 500 620 56))
    $linePen = New-RoundPen $WHITE 36
    $g.DrawLine($linePen, 372, 340, 712, 340)
    $g.DrawLine($linePen, 372, 440, 622, 440)

    Save-Png $bmp $g "concept-w5-stack.png"
}

# ---------- W6：剪贴板 + 复制角标 ----------
function New-W6 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 262 240 480 560 56))
    $g.DrawPath($pen, (New-RoundedPath 382 160 240 150 60))
    $g.DrawLine($pen, 442, 260, 582, 260)

    $badgePen = New-RoundPen $WHITE 40
    $g.DrawPath($badgePen, (New-RoundedPath 586 622 110 110 28))
    $g.DrawPath($badgePen, (New-RoundedPath 630 666 110 110 28))

    Save-Png $bmp $g "concept-w6-copy-badge.png"
}

# ---------- W7：剪贴板 + 向下箭头（粘贴） ----------
function New-W7 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 262 240 480 560 56))
    $g.DrawPath($pen, (New-RoundedPath 382 160 240 150 60))
    $g.DrawLine($pen, 442, 260, 582, 260)

    $arrow = New-RoundPen $WHITE 54
    $g.DrawLine($arrow, 512, 420, 512, 560)
    $g.DrawLine($arrow, 512, 560, 466, 514)
    $g.DrawLine($arrow, 512, 560, 558, 514)

    Save-Png $bmp $g "concept-w7-paste.png"
}

# ---------- W8：剪贴板 + 加号 ----------
function New-W8 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 262 240 480 560 56))
    $g.DrawPath($pen, (New-RoundedPath 382 160 240 150 60))
    $g.DrawLine($pen, 442, 260, 582, 260)
    $g.DrawLine($pen, 392, 520, 632, 520)
    $g.DrawLine($pen, 512, 410, 512, 630)

    Save-Png $bmp $g "concept-w8-plus.png"
}

# ---------- W9：大夹子（单独剪贴夹图形） ----------
function New-W9 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 342 180 340 520 90))
    $g.DrawLine((New-RoundPen $WHITE 44), 452, 500, 572, 500)
    $dotPen = New-RoundPen $WHITE 18
    $g.DrawEllipse($dotPen, 392, 250, 28, 28)
    $g.DrawEllipse($dotPen, 604, 250, 28, 28)

    Save-Png $bmp $g "concept-w9-clip.png"
}

# ---------- W10：剪贴板 + 圆形对勾角标 ----------
function New-W10 {
    $bmp, $g = New-Canvas
    $pen = New-RoundPen $WHITE 56

    $g.DrawPath($pen, (New-RoundedPath 262 240 480 560 56))
    $g.DrawPath($pen, (New-RoundedPath 382 160 240 150 60))
    $g.DrawLine($pen, 442, 260, 582, 260)

    $badge = New-RoundPen $WHITE 40
    $g.DrawEllipse($badge, 622, 640, 130, 130)
    $check = New-RoundPen $WHITE 36
    $pts = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(666, 702),
        [System.Drawing.PointF]::new(686, 722),
        [System.Drawing.PointF]::new(726, 676))
    $g.DrawLines($check, $pts)

    Save-Png $bmp $g "concept-w10-check-badge.png"
}

# ---------- 总览 2（W3 风格延伸，6 个，深色/靛蓝两行） ----------
function New-Preview2 {
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
    $g.DrawString("QuickClip - 白线透明背景 · W3 风格延伸", $titleFont, $titleBrush, $titleRect, $format)

    $labelFont = [System.Drawing.Font]::new("Segoe UI", 36, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 226, 232, 240))
    $labels = @("W5 双层卡片", "W6 复制角标", "W7 粘贴箭头", "W8 加号", "W9 大夹子", "W10 对勾角标")
    $names = @("concept-w5-stack.png", "concept-w6-copy-badge.png", "concept-w7-paste.png", "concept-w8-plus.png", "concept-w9-clip.png", "concept-w10-check-badge.png")
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

    Save-Png $bmp $g "preview-simple2.png"
}

New-W5
New-W6
New-W7
New-W8
New-W9
New-W10
New-Preview2
Write-Host "done"
