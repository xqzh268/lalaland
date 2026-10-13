param([string]$Executable = 'publish/LaLaLand-win-x64/LaLaLand.exe', [string]$ReportPath = 'output/lyric-stability-release-media.json')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exe = [IO.Path]::GetFullPath((Join-Path $projectRoot $Executable))
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $exe
$info.Arguments = '--media-worker'
$info.WorkingDirectory = Split-Path $exe -Parent
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$probe = [Diagnostics.Process]::Start($info)
try {
    $probe.StandardInput.WriteLine('{"Id":1,"Command":"monitor","Source":0,"PositionTicks":0}')
    $probe.StandardInput.Flush()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $seenTrack = $null
    $seenArtwork = $false
    $replySeen = $false
    $lineCount = 0
    while ($timer.Elapsed.TotalSeconds -lt 8) {
        $read = $probe.StandardOutput.ReadLineAsync()
        if (!$read.Wait(5000) -or $null -eq $read.Result) { break }
        $message = $read.Result | ConvertFrom-Json
        $lineCount++
        if ($message.Kind -eq 'reply' -and $message.Id -eq 1) {
            if ($message.Error) { throw $message.Error }
            $replySeen = $true
        }
        if ($message.Kind -eq 'snapshot') {
            if ($message.State.Track) { $seenTrack = $message.State.Track }
            if ($message.State.Artwork) {
                $art = $message.State.Artwork
                $pixels = [Convert]::FromBase64String($art.BackdropPixels)
                $seenArtwork = $art.Encoded.Length -gt 0 -and $pixels.Length -eq $art.BackdropSize * $art.BackdropSize * 4 -and @($art.LyricPalette | Where-Object { $_.A -ne 255 }).Count -eq 0
            }
        }
        if ($replySeen -and $seenTrack -and $seenArtwork) { break }
    }
    if (!$replySeen -or !$seenTrack -or !$seenArtwork) { throw 'Release media transport did not return a complete track and artwork.' }
    $probe.StandardInput.Close()
    if (!$probe.WaitForExit(3000)) { throw 'Release media worker did not stop after closing the pipe.' }
    if ($probe.ExitCode -ne 0) { throw "Release media worker exited with $($probe.ExitCode)." }
    $result = [PSCustomObject]@{ Passed=$true; Track=$seenTrack.Title; Artist=$seenTrack.Artist; Source=$seenTrack.SourceAppId; ArtworkPresent=$seenArtwork; SnapshotCount=$lineCount; ExitCode=$probe.ExitCode }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot $ReportPath) -Encoding utf8
    $result | ConvertTo-Json
} finally {
    if (!$probe.HasExited) { $probe.Kill() }
    $probe.Dispose()
}
