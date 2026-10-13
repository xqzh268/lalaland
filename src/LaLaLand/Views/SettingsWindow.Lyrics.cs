using LaLaLand.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LaLaLand.Views;

public sealed partial class SettingsWindow
{
    private string? _lyricTrackKey;
    private bool _lyricsClosed;
    private readonly CancellationTokenSource _importCancellation = new();

    private TrackInfo? LyricTrack => App.Media.Track is { } track && App.Media.Duration > TimeSpan.Zero
        ? track with { Duration = App.Media.Duration } : App.Media.Track;

    private void InitLyricsManagement()
    {
        App.Media.TrackChanged += UpdateLyricsManagement;
        App.Media.TimelineChanged += UpdateLyricsManagement;
        _lyrics.Changed += OnLyricsManagementChanged;
        UpdateLyricsManagement();
        Closed += (_, _) =>
        {
            _lyricsClosed = true;
            _importCancellation.Cancel();
            App.Media.TrackChanged -= UpdateLyricsManagement;
            App.Media.TimelineChanged -= UpdateLyricsManagement;
            _lyrics.Changed -= OnLyricsManagementChanged;
        };
    }

    private void OnLyricsManagementChanged(TrackInfo? _) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_lyricsClosed) UpdateLyricsManagement(force: true);
    });

    private void UpdateLyricsManagement() => UpdateLyricsManagement(force: false);

    private void UpdateLyricsManagement(bool force)
    {
        if (_lyricsClosed) return;
        var track = LyricTrack;
        var key = track is null ? null : LyricCalibration.Key(track);
        if (!force && key == _lyricTrackKey && LyricTrackText.Text != "开始播放歌曲后可校准") return;
        _lyricTrackKey = key;
        bool loading = _loading;
        _loading = true;
        var calibration = key is null ? LyricCalibration.Default
            : _settings.Current.SongLyricCalibrations.GetValueOrDefault(key) ?? LyricCalibration.Default;
        SongOffsetBox.Value = calibration.SafeOffsetMs;
        SongDriftBox.Value = Math.Round((calibration.SafeScale - 1) * 100, 3);
        SongOffsetBox.IsEnabled = SongDriftBox.IsEnabled = ResetCalibrationButton.IsEnabled =
            ImportLyricsButton.IsEnabled = track is not null;
        bool local = track is not null && _lyrics.HasLocalLyrics(track);
        RefreshLyricsButton.IsEnabled = track is not null && !local;
        RemoveLocalLyricsButton.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        LyricTrackText.Text = track is null ? "显示面板并开始播放歌曲后，可校准当前歌曲"
            : $"{track.Title} · {track.Artist} · {track.Album}（{track.Duration:m\\:ss}）";
        _loading = loading;
    }

    private void OnSongCalibrationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || !double.IsFinite(args.NewValue) || LyricTrack is not { } track ||
            !double.IsFinite(SongOffsetBox.Value) || !double.IsFinite(SongDriftBox.Value)) return;
        var key = LyricCalibration.Key(track);
        var calibration = new LyricCalibration((int)Math.Round(SongOffsetBox.Value), 1 + SongDriftBox.Value / 100);
        _settings.Update(s =>
        {
            if (calibration.SafeOffsetMs == 0 && Math.Abs(calibration.SafeScale - 1) < 0.000001) s.SongLyricCalibrations.Remove(key);
            else s.SongLyricCalibrations[key] = calibration;
        });
        LyricsActionStatus.Text = $"已保存本曲校准：{calibration.SafeOffsetMs:+0;-0;0}毫秒，累计偏差{SongDriftBox.Value:+0.###;-0.###;0}%";
    }

    private void OnResetCalibrationClick(object sender, RoutedEventArgs e)
    {
        if (LyricTrack is not { } track) return;
        _settings.Update(s => s.SongLyricCalibrations.Remove(LyricCalibration.Key(track)));
        UpdateLyricsManagement(force: true);
        LyricsActionStatus.Text = "已重置本曲校准，全局时间偏移继续生效";
    }

    private void OnRefreshLyricsClick(object sender, RoutedEventArgs e)
    {
        if (LyricTrack is not { } track) return;
        try
        {
            _lyrics.Refresh(track);
            LyricsActionStatus.Text = _settings.Current.LyricsEnabled ? "正在重新搜索本曲歌词…" : "已清除此曲缓存，开启歌词后会重新搜索";
        }
        catch (Exception ex) { LyricsActionStatus.Text = "重新搜索失败：" + ex.Message; }
    }

    private async void OnImportLyricsClick(object sender, RoutedEventArgs e)
    {
        if (LyricTrack is not { } track) return;
        ImportLyricsButton.IsEnabled = false;
        var token = _importCancellation.Token;
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            foreach (var extension in new[] { ".lrc", ".yrc", ".txt" }) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is null || token.IsCancellationRequested) return;
            await _lyrics.ImportAsync(track, file.Path, token);
            if (!_lyricsClosed) LyricsActionStatus.Text = $"已为《{track.Title}》导入{file.Name}，将优先使用本地歌词";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_lyricsClosed) LyricsActionStatus.Text = "导入失败：" + ex.Message; }
        finally { if (!_lyricsClosed) UpdateLyricsManagement(force: true); }
    }

    private void OnRemoveLocalLyricsClick(object sender, RoutedEventArgs e)
    {
        if (LyricTrack is not { } track) return;
        try
        {
            _lyrics.RemoveLocalLyrics(track);
            UpdateLyricsManagement(force: true);
            LyricsActionStatus.Text = "已移除本曲导入副本，恢复在线匹配（原文件保留）";
        }
        catch (Exception ex) { LyricsActionStatus.Text = "移除失败：" + ex.Message; }
    }
}
