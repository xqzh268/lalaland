# Builds an installable Velopack release (Setup.exe + update packages) from the current source.
# Pack id is LaLaLandApp so the install root (%LOCALAPPDATA%\LaLaLandApp) never overlaps the user data folder
# (%LOCALAPPDATA%\LaLaLand holds imported lyrics, which an uninstall would otherwise delete).
# Usage: ./tools/package-release.ps1 [-Upload] [-Notes path\to\notes.md]
#   Output: output/velopack/releases/ (git-ignored). -Upload publishes to GitHub Releases (needs `gh auth login`
#   or GITHUB_TOKEN). Install the CLI once with: dotnet tool install -g vpk
param([switch]$Upload, [string]$Notes)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$project = 'src/LaLaLand/LaLaLand.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$repo = 'https://github.com/xqzh268/lalaland'
$publish = "output/velopack/publish-$version"
$releases = "output/velopack/releases"

dotnet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained false -o $publish
if ($LASTEXITCODE) { throw 'publish failed' }

New-Item -ItemType Directory -Path $releases -Force | Out-Null
# Fetch previous packages so vpk can generate delta updates (ignored when there is no earlier release).
vpk download github --repoUrl $repo --outputDir $releases 2>&1 | Out-Host

$pack = @('pack', '--packId', 'LaLaLandApp', '--packVersion', $version, '--packDir', $publish, '--mainExe', 'LaLaLand.exe',
    '--packTitle', 'LaLaLand', '--packAuthors', 'LaLaLand', '--icon', 'src/LaLaLand/Assets/LaLaLand.ico',
    '--framework', 'net8.0-x64-desktop', '--outputDir', $releases)
if ($Notes) { $pack += @('--releaseNotes', $Notes) }
vpk @pack
if ($LASTEXITCODE) { throw 'vpk pack failed' }

if ($Upload) {
    vpk upload github --repoUrl $repo --outputDir $releases --publish --releaseName "LaLaLand v$version" --tag "v$version"
    if ($LASTEXITCODE) { throw 'vpk upload failed' }
}
Get-ChildItem $releases | Format-Table Name, Length
