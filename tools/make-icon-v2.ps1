# Packages the generated PNG as a Windows ICO, preserving its transparent corners.
param(
  [string]$Source = "$PSScriptRoot/../src/LaLaLand/Assets/LaLaLand-v2.png",
  [string]$Out = "$PSScriptRoot/../src/LaLaLand/Assets/LaLaLand-v2.ico"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [System.IO.Path]::GetFullPath($Out)
$outputDirectory = Split-Path -Path $outputPath -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$image = [System.Drawing.Image]::FromFile($sourcePath)
try {
  if ($image.Width -ne $image.Height) { throw 'The app icon source must be square.' }
  $frames = @()
  foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
      try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($image, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
      } finally { $graphics.Dispose() }
      $stream = New-Object System.IO.MemoryStream
      try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += , @($size, $stream.ToArray())
      } finally { $stream.Dispose() }
    } finally { $bitmap.Dispose() }
  }
} finally { $image.Dispose() }

$fileStream = [System.IO.File]::Create($outputPath)
$writer = New-Object System.IO.BinaryWriter $fileStream
try {
  $writer.Write([UInt16]0)
  $writer.Write([UInt16]1)
  $writer.Write([UInt16]$frames.Count)
  $offset = 6 + 16 * $frames.Count
  foreach ($frame in $frames) {
    $size = $frame[0]
    $dimension = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]$frame[1].Length)
    $writer.Write([UInt32]$offset)
    $offset += $frame[1].Length
  }
  foreach ($frame in $frames) { $writer.Write([byte[]]$frame[1]) }
} finally { $writer.Dispose() }

Write-Output "Wrote $outputPath"
