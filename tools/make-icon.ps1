# Draws Latch.ico (a padlock on a quiet blue rounded square) at 16-256 px and packs PNG frames into one .ico.
# Usage: pwsh tools/make-icon.ps1 src/Latch.Host/Latch.ico
param([string]$Out = "src/Latch.Host/Latch.ico")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $r = [single]($s * 0.22)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $m = [single]($s * 0.04); $w = [single]($s - 2 * $m)
  $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
  $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
  $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
  $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
  $path.CloseFigure()
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $s)), ([System.Drawing.Color]::FromArgb(255, 74, 128, 226)), ([System.Drawing.Color]::FromArgb(255, 44, 92, 190))
  $g.FillPath($brush, $path)
  $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
  $pen = New-Object System.Drawing.Pen $white, ([single][Math]::Max(1.5, $s * 0.085))
  $sw = [single]($s * 0.30); $sx = [single](($s - $sw) / 2); $sy = [single]($s * 0.20)
  $g.DrawArc($pen, $sx, $sy, $sw, [single]($s * 0.34), 180, 180)
  $g.DrawLine($pen, $sx, [single]($sy + $s * 0.17), $sx, [single]($s * 0.46))
  $g.DrawLine($pen, [single]($sx + $sw), [single]($sy + $s * 0.17), [single]($sx + $sw), [single]($s * 0.46))
  $bw = [single]($s * 0.48); $bx = [single](($s - $bw) / 2); $by = [single]($s * 0.43); $bh = [single]($s * 0.36)
  $body = New-Object System.Drawing.Drawing2D.GraphicsPath
  $br = [single]([Math]::Max(1, $s * 0.07))
  $body.AddArc($bx, $by, 2 * $br, 2 * $br, 180, 90)
  $body.AddArc($bx + $bw - 2 * $br, $by, 2 * $br, 2 * $br, 270, 90)
  $body.AddArc($bx + $bw - 2 * $br, $by + $bh - 2 * $br, 2 * $br, 2 * $br, 0, 90)
  $body.AddArc($bx, $by + $bh - 2 * $br, 2 * $br, 2 * $br, 90, 90)
  $body.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush $white), $body)
  if ($s -ge 24) {
    $kh = [single]($s * 0.09)
    $g.FillEllipse($brush, [single](($s - $kh) / 2), [single]($by + $bh * 0.32), $kh, $kh)
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
}
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter -ArgumentList $icoStream
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $d = if ($f.Size -ge 256) { 0 } else { $f.Size }
  $writer.Write([byte]$d); $writer.Write([byte]$d); $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$f.Bytes.Length); $writer.Write([uint32]$offset)
  $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $writer.Write($f.Bytes) }
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Out)), $icoStream.ToArray())
Write-Output "Wrote $Out"
