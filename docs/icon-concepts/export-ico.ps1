# 用法: pwsh export-ico.ps1 [concept-*.png]  默认 concept-4-cards.png
param([string]$Source = "concept-4-cards.png")

Add-Type -AssemblyName System.Drawing

# 脚本位于 docs\icon-concepts，仓库根目录为上两级
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$src = Join-Path $PSScriptRoot $Source
$icoPath = Join-Path $repoRoot "src\QuickClip\Assets\quickclip.ico"
$pngPath = Join-Path $repoRoot "icon.png"

if (-not (Test-Path $src)) { throw "找不到源图: $src" }

# 多尺寸缩略图（ICO 最大 256）
$sizes = 256, 128, 64, 48, 32, 24, 16
$srcBitmap = [System.Drawing.Bitmap]::new($src)
$pngs = @{}

foreach ($s in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcBitmap, 0, 0, $s, $s)
    $g.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $bmp.Dispose()
    $ms.Dispose()
}
$srcBitmap.Dispose()

# 组装 ICO：ICONDIR + ICONDIRENTRY + PNG 数据（Vista+ 支持 PNG 内嵌）
$fs = [System.IO.File]::Create($icoPath)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0)                       # reserved
$bw.Write([UInt16]1)                       # type = icon
$bw.Write([UInt16]$sizes.Count)            # image count
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # width (256 -> 0)
    $bw.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))   # height
    $bw.Write([Byte]0)                     # palette
    $bw.Write([Byte]0)                     # reserved
    $bw.Write([UInt16]1)                   # planes
    $bw.Write([UInt16]32)                  # bpp
    $bw.Write([UInt32]$pngs[$s].Length)    # bytes
    $bw.Write([UInt32]$offset)             # offset
    $offset += $pngs[$s].Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Close()
Write-Host "saved: $icoPath ($(Get-Item $icoPath).Length bytes)"

Copy-Item $src $pngPath -Force
Write-Host "saved: $pngPath"
