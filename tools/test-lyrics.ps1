param([switch]$Live)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $projectRoot 'tests\LaLaLand.Lyrics.Tests\LaLaLand.Lyrics.Tests.csproj'
$reportDirectory = Join-Path $projectRoot ('output\lyrics-acceptance\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
& dotnet run --project $project -c Release | Tee-Object -FilePath (Join-Path $reportDirectory 'regression.txt')
if ($LASTEXITCODE -ne 0) { throw 'Lyric regression checks failed.' }
if ($Live) {
    & dotnet run --project $project -c Release --no-build -- --live | Tee-Object -FilePath (Join-Path $reportDirectory 'yanhua-yileng.json')
    if ($LASTEXITCODE -ne 0) { throw 'Live recording match failed.' }
    & dotnet run --project $project -c Release --no-build -- --live-kugou | Tee-Object -FilePath (Join-Path $reportDirectory 'kugou.json')
    if ($LASTEXITCODE -ne 0) { throw 'Live Kugou lookup failed.' }
}
Write-Output "Reports: $reportDirectory"
