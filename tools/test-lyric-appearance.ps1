param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'src\LaLaLand\LaLaLand.csproj') -c Debug -p:Platform=x64 --no-restore -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
}
$testDirectory = Join-Path $projectRoot ('output\lyric-appearance-acceptance\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$hotkeys = @{}
foreach ($action in @('PlayPause','Next','Previous','ToggleFavorite','ToggleLyrics','ToggleIsland','VolumeUp','VolumeDown','ToggleMute')) { $hotkeys[$action] = $null }
@{ IsIslandVisible = $false; LyricsEnabled = $false; Hotkeys = $hotkeys; LyricFontFamily = 'Eras Demi ITC' } |
    ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testDirectory 'settings.json') -Encoding utf8
$exe = Join-Path $projectRoot 'src\LaLaLand\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\LaLaLand.exe'
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.WorkingDirectory = Split-Path -Path $exe -Parent
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.EnvironmentVariables['LALALAND_PANEL_ACCEPTANCE_DIR'] = $testDirectory
$info.EnvironmentVariables['LALALAND_LYRIC_APPEARANCE_TEST'] = '1'
$process = [Diagnostics.Process]::Start($info)
try {
    if (!$process.WaitForExit(45000)) { throw 'Lyric appearance acceptance timed out.' }
    $reportPath = Join-Path $testDirectory 'lyric-appearance-acceptance.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    [PSCustomObject]@{ Passed=$report.passed; CheckCount=$report.checks.Count; Palette=$report.palette; Report=$reportPath; Error=$report.error } | ConvertTo-Json -Depth 4
    if (!$report.passed) { throw 'Lyric appearance acceptance failed; inspect the report.' }
} finally {
    if (!$process.HasExited) { $process.Kill() }
    $process.Dispose()
}
