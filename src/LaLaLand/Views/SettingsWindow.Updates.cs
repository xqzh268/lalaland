using LaLaLand.Services;
using Microsoft.UI.Xaml;

namespace LaLaLand.Views;

public sealed partial class SettingsWindow
{
    private void InitUpdates()
    {
        void Refresh() => DispatcherQueue.TryEnqueue(RefreshUpdateUi);
        App.Updates.Changed += Refresh;
        Closed += (_, _) => App.Updates.Changed -= Refresh;
        VersionText.Text = $"版本 {App.Updates.CurrentVersion} · 轻量、置顶的音乐灵动岛";
        RefreshUpdateUi();
    }

    /// <summary>Opens the About page (used by the tray's update entry).</summary>
    public void ShowAbout()
    {
        Nav.SelectedItem = Nav.MenuItems[^1];
        if (App.Updates.AvailableVersion is null) _ = App.Updates.CheckAsync();
    }

    private void RefreshUpdateUi()
    {
        var updates = App.Updates;
        if (!string.IsNullOrEmpty(updates.Status)) UpdateStatusText.Text = updates.Status;
        bool available = updates.AvailableVersion is not null;
        InstallUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        InstallUpdateButton.Content = updates.UpdateReady ? "重启并安装" : "下载并更新";
        var notes = updates.ReleaseNotes?.Trim();
        UpdateNotesText.Text = notes ?? "";
        UpdateNotesText.Visibility = available && !string.IsNullOrEmpty(notes) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try { await App.Updates.CheckAsync(); }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        try
        {
            if (!App.Updates.UpdateReady)
            {
                UpdateProgress.Value = 0;
                UpdateProgress.Visibility = Visibility.Visible;
                bool ok = await App.Updates.DownloadAsync(p => DispatcherQueue.TryEnqueue(() => UpdateProgress.Value = p));
                UpdateProgress.Visibility = Visibility.Collapsed;
                if (!ok) return;
            }
            if (App.Updates.ApplyOnExit())
                App.QuitForUpdate();
        }
        finally { InstallUpdateButton.IsEnabled = true; }
    }
}
