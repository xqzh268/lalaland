param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SkipBuild) {
  & dotnet build (Join-Path $projectRoot 'src\LaLaLand\LaLaLand.csproj') -c Debug -p:Platform=x64 --no-restore -v:minimal
  if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
}
$testDirectory = Join-Path $projectRoot ('output\panel-acceptance\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
# Dedicated settings and verified free keys: avoid the user's settings, instance lock and existing hotkeys.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LaLaLandAcceptanceKeys {
  [DllImport("user32.dll", SetLastError=true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
'@
$freeKeys = @()
foreach ($key in @(65..90) + @(48..57)) {
  if ([LaLaLandAcceptanceKeys]::RegisterHotKey([IntPtr]::Zero, 12000 + $key, 16391, $key)) {
    [LaLaLandAcceptanceKeys]::UnregisterHotKey([IntPtr]::Zero, 12000 + $key) | Out-Null
    $freeKeys += $key
    if ($freeKeys.Count -eq 9) { break }
  }
}
if ($freeKeys.Count -lt 9) { throw 'Not enough free test hotkeys.' }
$hotkeys = [ordered]@{}
$actions = 'PlayPause','Next','Previous','ToggleFavorite','ToggleLyrics','ToggleIsland','VolumeUp','VolumeDown','ToggleMute'
for ($i = 0; $i -lt $actions.Count; $i++) { $hotkeys[$actions[$i]] = @{ Modifiers = 7; Key = $freeKeys[$i] } }
# This fixture explicitly needs a running animation clock before hiding. Do not let an unrelated full-screen
# app select the optimized game policy and stop it before the lifecycle assertions run.
$settings = @{ IsIslandVisible = $false; HideWhenIdle = $false; LyricsEnabled = $false; GamePresentation = 'Full'; Hotkeys = $hotkeys }
$settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testDirectory 'settings.json') -Encoding utf8
$exe = Join-Path $projectRoot 'src\LaLaLand\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\LaLaLand.exe'
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $exe
$startInfo.WorkingDirectory = Split-Path -Path $exe -Parent
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.EnvironmentVariables['LALALAND_PANEL_ACCEPTANCE_DIR'] = $testDirectory
$process = [System.Diagnostics.Process]::Start($startInfo)
try {
  if (!$process.WaitForExit(45000)) { throw "Acceptance run timed out (PID $($process.Id))." }
  $reportPath = Join-Path $testDirectory 'panel-acceptance.json'
  if (!(Test-Path -LiteralPath $reportPath)) { throw "No acceptance report. Check $testDirectory." }
  $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
  if (!$report.passed) { throw $report.error }
  [PSCustomObject]@{ Passed = $report.passed; CheckCount = $report.checks.Count; IdleSample = $report.hiddenIdleSample; Report = $reportPath } | ConvertTo-Json -Depth 4
} finally {
  if (!$process.HasExited) { $process.Kill() }
  $process.Dispose()
}
