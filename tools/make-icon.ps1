# Generates src/LaLaLand/Assets/LaLaLand.ico (PNG-compressed frames) with System.Drawing.
param([string]$Out = "$PSScriptRoot/../src/LaLaLand/Assets/LaLaLand.ico")
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = @()
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure(); return $p
}
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
  # Background squircle with deep gradient
  $bg = RoundRect 0 0 $s $s ($s * 0.24)
  $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 32, 30, 48)), ([System.Drawing.Color]::FromArgb(255, 8, 8, 12))
  $g.FillPath($bgBrush, $bg)
  # Island pill
  $pw = $s * 0.74; $ph = $s * 0.30; $px = ($s - $pw) / 2; $py = ($s - $ph) / 2
  $pill = RoundRect $px $py $pw $ph ($ph / 2)
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)), $pill)
  $glow = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF $px, 0), (New-Object System.Drawing.PointF ($px + $pw), 0), ([System.Drawing.Color]::FromArgb(255, 255, 94, 132)), ([System.Drawing.Color]::FromArgb(255, 132, 108, 255))
  # Cover dot + equalizer bars inside pill
  $dot = $ph * 0.62; $g.FillEllipse($glow, $px + ($ph - $dot) / 2, $py + ($ph - $dot) / 2, $dot, $dot)
  $barW = [Math]::Max(1.0, $s * 0.045); $gap = $barW * 0.8
  $heights = 0.55, 0.85, 0.4, 0.7
  $bx = $px + $pw - $ph * 0.45 - 4 * $barW - 3 * $gap
  for ($i = 0; $i -lt 4; $i++) {
    $bh = $ph * 0.62 * $heights[$i]
    $g.FillRectangle($glow, [float]($bx + $i * ($barW + $gap)), [float]($py + ($ph - $bh) / 2), [float]$barW, [float]$bh)
  }
  if ($s -ge 32) {
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 255, 255, 255)), ([Math]::Max(1, $s / 64))
    $g.DrawPath($pen, $pill)
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  $frames += , @($s, $ms.ToArray())
}
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
  $s = $f[0]; $len = $f[1].Length
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$len); $w.Write([UInt32]$offset); $offset += $len
}
foreach ($f in $frames) { $w.Write($f[1]) }
$w.Close()
Write-Output "Wrote $Out"
