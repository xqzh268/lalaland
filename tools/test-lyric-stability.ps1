param([ValidateSet('status','fonts','lookup')][string]$Mode = 'status', [switch]$WithMedia, [switch]$CrashMedia, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'src/LaLaLand/LaLaLand.csproj') -c Debug -p:Platform=x64 --no-restore -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
}
$testDirectory = Join-Path $projectRoot ('output/lyric-stability-acceptance/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff') + '-' + $Mode + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$hotkeys = @{}
foreach ($action in @('PlayPause','Next','Previous','ToggleFavorite','ToggleLyrics','ToggleIsland','VolumeUp','VolumeDown','ToggleMute')) { $hotkeys[$action] = $null }
@{ IsIslandVisible = $false; LyricsEnabled = $false; Hotkeys = $hotkeys } | ConvertTo-Json -Depth 3 |
    Set-Content -LiteralPath (Join-Path $testDirectory 'settings.json') -Encoding utf8
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $projectRoot 'src/LaLaLand/bin/x64/Debug/net8.0-windows10.0.22621.0/win-x64/LaLaLand.exe'
$info.WorkingDirectory = Split-Path -Path $info.FileName -Parent
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.EnvironmentVariables['LALALAND_PANEL_ACCEPTANCE_DIR'] = $testDirectory
$info.EnvironmentVariables['LALALAND_LYRIC_STABILITY_TEST'] = $Mode
if ($WithMedia) { $info.EnvironmentVariables['LALALAND_STABILITY_WITH_MEDIA'] = '1' }
if ($CrashMedia) { $info.EnvironmentVariables['LALALAND_STABILITY_CRASH_MEDIA'] = '1' }
$process = [Diagnostics.Process]::Start($info)
try {
    if (!$process.WaitForExit(45000)) { throw 'Stability acceptance timed out.' }
    $reportPath = Join-Path $testDirectory 'lyric-stability-acceptance.json'
    if (!(Test-Path -LiteralPath $reportPath)) { throw ('Native crash: 0x{0:X8}; inspect {1}' -f $process.ExitCode, $testDirectory) }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    [PSCustomObject]@{ Passed=$report.passed; Checks=$report.checks.Count; Cycles=$report.completedCycles; WithMedia=$report.withMedia; Report=$reportPath; Error=$report.error } | ConvertTo-Json -Depth 4
    if (!$report.passed) { throw 'Lyric stability acceptance failed.' }
} finally {
    if (!$process.HasExited) { $process.Kill() }
    $process.Dispose()
}
