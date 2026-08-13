# Capture README screenshots from a live QuickClip window.
# Backs up the user's database/settings and restores them afterwards.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Cap {
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exe = Join-Path $root 'src\QuickClip\bin\Debug\net8.0-windows10.0.19041.0\QuickClip.exe'
$outDir = Join-Path $root 'docs\assets'
if (-not (Test-Path $exe)) { throw "QuickClip.exe not found: $exe" }

$dataDir = Join-Path $env:LOCALAPPDATA 'QuickClip'
$dbPath = Join-Path $dataDir 'quickclip.db'
$settingsPath = Join-Path $dataDir 'settings.json'
$previewDir = Join-Path $dataDir 'previews'
$dbBak = Join-Path $dataDir 'quickclip.db.readme-bak'
$settingsBak = Join-Path $dataDir 'settings.json.readme-bak'
$diagramPath = Join-Path $previewDir 'readme-shot-diagram.png'
$qrPath = Join-Path $previewDir 'readme-shot-qr.png'

function Stop-QuickClip {
  Get-Process QuickClip -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
}

function Find-QuickClipWindow([int]$pidValue) {
  $script:found = [IntPtr]::Zero
  $targetPid = [uint32]$pidValue
  [Cap]::EnumWindows({
    param([IntPtr]$h, [IntPtr]$lp)
    [uint32]$wpid = 0
    [void][Cap]::GetWindowThreadProcessId($h, [ref]$wpid)
    if ($wpid -ne $targetPid) { return $true }
    if (-not [Cap]::IsWindowVisible($h)) { return $true }
    $len = [Cap]::GetWindowTextLength($h)
    $sb = New-Object Text.StringBuilder ($len + 1)
    [void][Cap]::GetWindowText($h, $sb, $sb.Capacity)
    if ($sb.ToString() -ne 'QuickClip') { return $true }
    $r = New-Object Cap+RECT
    [void][Cap]::GetWindowRect($h, [ref]$r)
    if (($r.Right - $r.Left) -gt 200 -and ($r.Bottom - $r.Top) -gt 200) { $script:found = $h }
    return $true
  }, [IntPtr]::Zero) | Out-Null
  return $script:found
}

function Get-VisibleRect([IntPtr]$hwnd) {
  $r = New-Object Cap+RECT
  $ok = [Cap]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, [Runtime.InteropServices.Marshal]::SizeOf([type][Cap+RECT]))
  if ($ok -ne 0 -or ($r.Right - $r.Left) -lt 200) {
    [void][Cap]::GetWindowRect($hwnd, [ref]$r)
  }
  return $r
}

function Save-WindowPng([IntPtr]$hwnd, [string]$path) {
  $r = Get-VisibleRect $hwnd
  Save-ScreenRect $r.Left $r.Top ($r.Right - $r.Left) ($r.Bottom - $r.Top) $path
}

function Save-ScreenRect([int]$x, [int]$y, [int]$w, [int]$h, [string]$path) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "OK $path ($((Get-Item $path).Length) bytes)"
}

function New-DiagramPng([string]$path) {
  $bmp = New-Object System.Drawing.Bitmap 880, 280
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.TextRenderingHint = 'ClearTypeGridFit'
  $g.Clear([System.Drawing.Color]::FromArgb(27, 27, 31))
  $titleFont = New-Object System.Drawing.Font('Segoe UI', 16)
  $bodyFont = New-Object System.Drawing.Font('Segoe UI', 12)
  $smallFont = New-Object System.Drawing.Font('Segoe UI', 10)
  $white = [System.Drawing.Brushes]::White
  $muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(160, 170, 180))
  $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(52, 211, 153))
  $card = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(38, 38, 44))
  $g.DrawString('Clipboard pipeline', $titleFont, $white, 24, 16)
  $g.DrawString('Win+V  ->  parse  ->  SQLite  ->  paste', $smallFont, $muted, 24, 46)
  $boxes = @(
    @{ T = 'Hook'; S = 'WH_KEYBOARD_LL'; X = 28; C = [System.Drawing.Color]::FromArgb(99, 102, 241) },
    @{ T = 'Capture'; S = 'text / image / link'; X = 240; C = [System.Drawing.Color]::FromArgb(56, 189, 248) },
    @{ T = 'QR / OCR'; S = 'offline'; X = 470; C = [System.Drawing.Color]::FromArgb(52, 211, 153) },
    @{ T = 'History'; S = '233 + 24h'; X = 680; C = [System.Drawing.Color]::FromArgb(251, 191, 36) }
  )
  foreach ($b in $boxes) {
    $brush = New-Object System.Drawing.SolidBrush $b.C
    $g.FillRectangle($card, $b.X, 84, 172, 80)
    $g.FillRectangle($brush, $b.X, 84, 6, 80)
    $g.DrawString($b.T, $titleFont, $white, $b.X + 18, 98)
    $g.DrawString($b.S, $smallFont, $muted, $b.X + 18, 130)
    $brush.Dispose()
  }
  $g.DrawString('QuickClip   local first   no cloud', $smallFont, $muted, 24, 236)
  $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $titleFont.Dispose(); $bodyFont.Dispose(); $smallFont.Dispose()
  $muted.Dispose(); $accent.Dispose(); $card.Dispose()
}

function New-QrPng([string]$path) {
  $src = Join-Path $root 'docs\assets\readme-src\qr-github.png'
  if (-not (Test-Path $src)) { throw "QR sample missing: $src" }
  Copy-Item $src $path -Force
}

function Seed-Database {
  New-Item -ItemType Directory -Force -Path $previewDir | Out-Null
  New-DiagramPng $diagramPath
  New-QrPng $qrPath
  $sql = @"
DELETE FROM clipboard_items;
INSERT INTO clipboard_items (content_type, text_content, preview_path, qr_content, char_count, is_pinned, created_at) VALUES
('Text', 'Win + V to open QuickClip', NULL, NULL, 24, 0, '2026-08-13 09:10:00'),
('Text', 'Pinyin search: sjjg', NULL, NULL, 18, 0, '2026-08-13 09:20:00'),
('Link', 'https://github.com/soldier-cv/quick-clip', NULL, NULL, 40, 0, '2026-08-13 09:30:00'),
('Image', NULL, '$($diagramPath.Replace('\','/'))', NULL, 42000, 0, '2026-08-13 09:40:00'),
('Image', NULL, '$($qrPath.Replace('\','/'))', 'https://github.com/soldier-cv/quick-clip', 18200, 1, '2026-08-13 09:50:00');
"@
  $py = Get-Command python -ErrorAction SilentlyContinue
  if ($py) {
    $tmp = Join-Path $env:TEMP 'qc-seed-readme.py'
    @"
import sqlite3
con = sqlite3.connect(r'$dbPath')
con.executescript(r'''$sql''')
con.close()
"@ | Set-Content -Path $tmp -Encoding UTF8
    & python $tmp
    Remove-Item $tmp -Force
    return
  }
  $sqliteDll = Join-Path (Split-Path $exe -Parent) 'Microsoft.Data.Sqlite.dll'
  Add-Type -Path $sqliteDll
  $con = New-Object Microsoft.Data.Sqlite.SqliteConnection "Data Source=$dbPath"
  $con.Open()
  $cmd = $con.CreateCommand()
  $cmd.CommandText = $sql
  [void]$cmd.ExecuteNonQuery()
  $con.Close()
}

function Restore-UserData {
  Stop-QuickClip
  if (Test-Path $dbBak) {
    Copy-Item $dbBak $dbPath -Force
    Remove-Item $dbBak -Force
  }
  if (Test-Path $settingsBak) {
    Copy-Item $settingsBak $settingsPath -Force
    Remove-Item $settingsBak -Force
  }
  foreach ($f in @($diagramPath, $qrPath)) {
    if (Test-Path $f) { Remove-Item $f -Force }
  }
}

Stop-QuickClip
New-Item -ItemType Directory -Force -Path $dataDir, $previewDir, $outDir | Out-Null
if (Test-Path $dbPath) { Copy-Item $dbPath $dbBak -Force }
if (Test-Path $settingsPath) { Copy-Item $settingsPath $settingsBak -Force }

$hostHwnd = [IntPtr]::Zero
try {
  $settings = @{
    WindowAlwaysOnTop = $true
    Theme = 'Terminal'
    AutoCheckUpdates = $false
    AutoStart = $false
  }
  if (Test-Path $settingsBak) {
    $existing = Get-Content $settingsBak -Raw | ConvertFrom-Json
    $existing.WindowAlwaysOnTop = $true
    $existing.Theme = 'Terminal'
    $existing.AutoCheckUpdates = $false
    $existing | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
  } else {
    $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8
  }

  Seed-Database

  try { Set-Clipboard -Value 'https://github.com/soldier-cv/quick-clip' } catch { }

  $hostHwnd = (Get-Process -Id $PID).MainWindowHandle
  if ($hostHwnd -ne [IntPtr]::Zero) {
    [void][Cap]::ShowWindow($hostHwnd, 6)
  }

  $proc = Start-Process -FilePath $exe -PassThru
  Start-Sleep -Seconds 4
  $hwnd = Find-QuickClipWindow $proc.Id
  if ($hwnd -eq [IntPtr]::Zero) { throw 'QuickClip window not found' }

  $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  $posX = $wa.Left + 72
  $posY = $wa.Top + 48
  [void][Cap]::ShowWindow($hwnd, 9)
  # Move only — do not resize (WPF size is DIP; SetWindowPos is pixels).
  [void][Cap]::SetWindowPos($hwnd, [IntPtr]::Zero, $posX, $posY, 0, 0, 0x0001 -bor 0x0040)
  [void][Cap]::SetForegroundWindow($hwnd)
  Start-Sleep -Milliseconds 1200

  Save-WindowPng $hwnd (Join-Path $outDir 'preview.png')

  $r = Get-VisibleRect $hwnd
  $w = $r.Right - $r.Left
  $hh = $r.Bottom - $r.Top
  $cardX = $r.Left + [int]($w * 0.42)
  $imgY = $r.Top + [int]($hh * 0.22)
  [void][Cap]::SetCursorPos($cardX, $imgY)
  Start-Sleep -Milliseconds 800
  $pad = 16
  $popupW = 500
  Save-ScreenRect ($r.Left - $pad) ($r.Top - $pad) ($w + $popupW + $pad * 2) ($hh + $pad * 2) (Join-Path $outDir 'preview-image.png')

  $qrY = $r.Top + [int]($hh * 0.48)
  [void][Cap]::SetCursorPos($cardX, $qrY)
  Start-Sleep -Milliseconds 700
  Save-ScreenRect ($r.Left - $pad) ($r.Top - $pad) ($w + $popupW + $pad * 2) ($hh + $pad * 2) (Join-Path $outDir 'qr-decode.png')

  $linkY = $r.Top + [int]($hh * 0.74)
  [void][Cap]::SetCursorPos($cardX, $linkY)
  Start-Sleep -Milliseconds 450
  [void][Cap]::SetCursorPos($r.Right - [int]($w * 0.24), $linkY)
  Start-Sleep -Milliseconds 1100
  [Cap]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 50
  [Cap]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 900
  Save-WindowPng $hwnd (Join-Path $outDir 'qr-generate.png')
}
finally {
  Restore-UserData
  if ($hostHwnd -and $hostHwnd -ne [IntPtr]::Zero) {
    [void][Cap]::ShowWindow($hostHwnd, 9)
  }
}
