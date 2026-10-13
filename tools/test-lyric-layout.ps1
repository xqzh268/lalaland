param([string]$FixturePath, [string]$Font = 'Eras Demi ITC', [string]$ChineseFont, [switch]$SkipBuild, [switch]$NoObstacle)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$FixturePath) {
    $fixture = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'LaLaLand\lyrics') -Filter '*.json' | Where-Object {
        try { (Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).Title -eq 'Cheap Hotel' } catch { $false }
    } | Select-Object -First 1
    if (!$fixture) { throw 'No Cheap Hotel lyric cache. Specify -FixturePath.' }
    $FixturePath = $fixture.FullName
}
if (!$SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'src\LaLaLand\LaLaLand.csproj') -c Debug -p:Platform=x64 --no-restore -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
}
$testDirectory = Join-Path $projectRoot ('output\lyric-layout-acceptance\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$hotkeys = @{}
foreach ($action in @('PlayPause','Next','Previous','ToggleFavorite','ToggleLyrics','ToggleIsland','VolumeUp','VolumeDown','ToggleMute')) { $hotkeys[$action] = $null }
@{ IsIslandVisible = $false; LyricsEnabled = $false; Hotkeys = $hotkeys } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $testDirectory 'settings.json') -Encoding utf8
$exe = Join-Path $projectRoot 'src\LaLaLand\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\LaLaLand.exe'
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $exe
$startInfo.WorkingDirectory = Split-Path -Path $exe -Parent
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.EnvironmentVariables['LALALAND_PANEL_ACCEPTANCE_DIR'] = $testDirectory
$startInfo.EnvironmentVariables['LALALAND_LYRIC_LAYOUT_FIXTURE'] = [System.IO.Path]::GetFullPath($FixturePath)
$startInfo.EnvironmentVariables['LALALAND_LYRIC_LAYOUT_FONT'] = $Font
if ($PSBoundParameters.ContainsKey('ChineseFont')) { $startInfo.EnvironmentVariables['LALALAND_LYRIC_LAYOUT_CHINESE_FONT'] = $ChineseFont }
if ($NoObstacle) { $startInfo.EnvironmentVariables['LALALAND_LYRIC_LAYOUT_NO_OBSTACLE'] = '1' }
$process = [System.Diagnostics.Process]::Start($startInfo)
try {
    if (!$process.WaitForExit(45000)) { throw 'Lyric layout acceptance timed out.' }
    $reportPath = Join-Path $testDirectory 'lyric-layout-acceptance.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    $worst = @($report.cases | Sort-Object @{Expression='passed'; Ascending=$true}, @{Expression='initialJumpPixels'; Descending=$true}, @{Expression='maxFrameStepPixels'; Descending=$true} | Select-Object -First 3)
    [PSCustomObject]@{ Passed = $report.passed; CaseCount = $report.cases.Count; FailedCases = $report.failedCaseCount; Worst = $worst; Report = $reportPath; Error = $report.error } | ConvertTo-Json -Depth 5
    if (!$report.passed) { throw 'Lyric layout or transition acceptance failed; inspect the report.' }
} finally {
    if (!$process.HasExited) { $process.Kill() }
    $process.Dispose()
}
