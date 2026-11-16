# Captures the screen region of the top-level window(s) owned by a process (default: Latch), or the whole screen.
param([string]$Out, [string]$Process = "Latch", [switch]$Full, [int]$MaxWidth = 100000, [int]$Id = 0)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class W {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static int MaxW = 100000; public static List<RECT> Rects(uint pid) { var l = new List<RECT>(); EnumWindows((h, x) => { uint p; GetWindowThreadProcessId(h, out p); RECT r; if (p == pid && IsWindowVisible(h) && DwmGetWindowAttribute(h, 9, out r, 16) == 0 && r.R - r.L > 50 && r.R - r.L < MaxW) l.Add(r); return true; }, IntPtr.Zero); return l; }
}
"@
[W]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
if ($Full) { $x=0;$y=0;$w=3440;$h=1440 } else {
  $p = if ($Id) { Get-Process -Id $Id } else { Get-Process $Process | Select-Object -First 1 }
  [W]::MaxW = $MaxWidth; $rs = [W]::Rects([uint32]$p.Id)
  if ($rs.Count -eq 0) { Write-Output "no window"; exit 1 }
  $x = ($rs | % { $_.L } | Measure -Minimum).Minimum - 24; $y = ($rs | % { $_.T } | Measure -Minimum).Minimum - 24
  $w = ($rs | % { $_.R } | Measure -Maximum).Maximum + 24 - $x; $h = ($rs | % { $_.B } | Measure -Maximum).Maximum + 24 - $y
}
$w=[int]$w; $h=[int]$h; $x=[int]$x; $y=[int]$y
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
$bmp.Save($Out)
Write-Output "$x,$y ${w}x$h"
