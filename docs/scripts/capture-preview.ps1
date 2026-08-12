# Capture QuickClip main window -> docs/assets/preview.png
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
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
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$exe = Join-Path $root 'src\QuickClip\bin\Debug\net8.0-windows10.0.19041.0\QuickClip.exe'
$out = Join-Path $root 'docs\assets\preview.png'
if (-not (Test-Path $exe)) { throw "QuickClip.exe not found: $exe" }

Get-Process QuickClip -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$proc = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3

$found = [IntPtr]::Zero
$targetPid = [uint32]$proc.Id
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
  $w = $r.Right - $r.Left
  $hh = $r.Bottom - $r.Top
  if ($w -gt 200 -and $hh -gt 200) { $script:found = $h }
  return $true
}, [IntPtr]::Zero) | Out-Null

if ($found -eq [IntPtr]::Zero) { throw 'QuickClip window not found' }

[void][Cap]::ShowWindow($found, 9)
[void][Cap]::SetWindowPos($found, [IntPtr]::Zero, 100, 80, 0, 0, 0x0001 -bor 0x0040)
[void][Cap]::SetForegroundWindow($found)
Start-Sleep -Milliseconds 800

$r = New-Object Cap+RECT
[void][Cap]::GetWindowRect($found, [ref]$r)
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
Write-Host "Capture $($r.Left),$($r.Top) ${w}x${hh}"

$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
if (-not [Cap]::PrintWindow($found, $hdc, 2)) {
  [void][Cap]::PrintWindow($found, $hdc, 0)
}
$g.ReleaseHdc($hdc)
$g.Dispose()

$sum = 0L
for ($i = 0; $i -lt 30; $i++) {
  $c = $bmp.GetPixel(10 + ($i * 13) % ($w - 20), 10 + ($i * 19) % ($hh - 20))
  $sum += $c.R + $c.G + $c.B
}
$avg = $sum / 30.0 / 3.0
Write-Host "brightness=$avg"
if ($avg -lt 8) {
  $g2 = [System.Drawing.Graphics]::FromImage($bmp)
  $g2.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
  $g2.Dispose()
}

$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$len = (Get-Item $out).Length
Write-Host "OK $out ($len bytes)"
