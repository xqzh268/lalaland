using LaLaLand.Interop;
using LaLaLand.Models;
using LaLaLand.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace LaLaLand.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly LyricsService _lyrics;
    private readonly Dictionary<HotkeyAction, (Button Button, TextBlock Warning)> _hotkeyRows = new();
    private HotkeyAction? _recording;
    // True until Load() finishes: controls raise ValueChanged while InitializeComponent() creates them (e.g. a
    // Slider clamping 0 up to its Minimum), and those must never be written back as user changes.
    private bool _loading = true;

    public SettingsWindow(SettingsService settings, HotkeyService hotkeys, LyricsService lyrics)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _lyrics = lyrics;
        InitializeComponent();
        Nav.SelectedItem = Nav.MenuItems[0];

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "LaLaLand-v2.ico"));
        double scale = Native.GetDpiForWindow(hwnd) / 96.0;
        var size = new SizeInt32((int)(860 * scale), (int)(760 * scale));
        var work = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        appWindow.MoveAndResize(new RectInt32(
            work.X + (work.Width - size.Width) / 2, work.Y + (work.Height - size.Height) / 2,
            Math.Min(size.Width, work.Width), Math.Min(size.Height, work.Height)));

        VersionText.Text = $"版本 {typeof(App).Assembly.GetName().Version?.ToString(3)} · 轻量、置顶的音乐灵动岛";
        BuildHotkeyRows();
        Load();
        InitUpdates();
        InitLyricAppearance();
        InitLyricsManagement();

        _hotkeys.ConflictsChanged += UpdateHotkeyRows;
        _settings.Changed += UpdatePanelVisibilityToggle;
        Closed += (_, _) =>
        {
            _hotkeys.ConflictsChanged -= UpdateHotkeyRows;
            _settings.Changed -= UpdatePanelVisibilityToggle;
            StopRecording();
        };
    }

    public void BringToFront()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Native.SetForegroundWindow(hwnd);
    }

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;

        PositionBox.SelectedIndex = s.Position switch
        {
            IslandPosition.TopLeft => 1,
            IslandPosition.TopRight => 2,
            IslandPosition.BottomLeft => 3,
            IslandPosition.BottomCenter => 4,
            IslandPosition.BottomRight => 5,
            _ => 0,
        };

        DisplayBox.Items.Clear();
        DisplayBox.Items.Add("主显示器");
        var displays = DisplayArea.FindAll();
        for (int i = 0; i < displays.Count; i++)
            DisplayBox.Items.Add($"显示器 {i + 1}（{displays[i].OuterBounds.Width}×{displays[i].OuterBounds.Height}）");
        DisplayBox.SelectedIndex = s.DisplayIndex >= 0 && s.DisplayIndex < displays.Count ? s.DisplayIndex + 1 : 0;

        OffsetXBox.Value = s.OffsetX;
        OffsetYBox.Value = s.OffsetY;
        CollapsedStyleBox.SelectedIndex = (int)s.CollapsedStyle;
        CollapsedWidthSlider.Value = s.CollapsedWidth;
        ExpandedWidthSlider.Value = s.ExpandedWidth;
        FitLyricsToggle.IsOn = s.FitCollapsedToLyrics;
        TwoLineToggle.IsOn = s.CollapsedTwoLineLyrics;
        LoadLyricAppearance();
        MaterialBox.SelectedIndex = (int)s.Material;
        SolidColorBox.Items.Clear();
        foreach (var (name, _) in SolidColors)
            SolidColorBox.Items.Add(name);
        int colorIndex = Array.FindIndex(SolidColors, c => c.Hex.Equals(s.SolidColor, StringComparison.OrdinalIgnoreCase));
        SolidColorBox.SelectedIndex = colorIndex >= 0 ? colorIndex : 0;
        SolidColorCard.Visibility = s.Material == IslandMaterial.Solid ? Visibility.Visible : Visibility.Collapsed;
        CornerRadiusSlider.Value = s.CornerRadius;
        DimSlider.Value = Math.Round(s.BackdropDim * 100);
        ScaleSlider.Value = Math.Round(s.Scale * 100);
        VisualizerToggle.IsOn = s.ShowVisualizer;
        DropletToggle.IsOn = s.DropletEffect;
        FlowToggle.IsOn = s.LyricFlowAroundCover;
        SweepToggle.IsOn = s.WordSweep;

        HoverDelaySlider.Value = s.HoverExpandDelayMs;
        CollapseDelaySlider.Value = s.CollapseDelayMs;
        PeekToggle.IsOn = s.PeekOnTrackChange;
        ClickLyricsToggle.IsOn = s.ClickToShowLyrics;
        HideIdleToggle.IsOn = s.HideWhenIdle;
        HidePanelToggle.IsOn = !s.IsIslandVisible;
        TopmostToggle.IsOn = s.TopmostGuard;
        YieldFullscreenToggle.IsOn = s.YieldToExclusiveFullscreen;
        GamePresentationBox.SelectedIndex = (int)s.GamePresentation;
        VolumeStepSlider.Value = Math.Round(s.VolumeStep * 100);
        StartupToggle.IsOn = StartupService.IsEnabled;
        AutoUpdateToggle.IsOn = s.AutoCheckUpdates;

        SourceBox.SelectedIndex = (int)s.Source;

        LyricsToggle.IsOn = s.LyricsEnabled;
        TranslationToggle.IsOn = s.ShowTranslation;
        LyricOffsetSlider.Value = s.LyricOffsetMs;

        UpdateHotkeyRows();
        _loading = false;
    }

    // ---------------------------------------------------------------- handlers

    private void UpdatePanelVisibilityToggle()
    {
        bool loading = _loading;
        _loading = true;
        HidePanelToggle.IsOn = !_settings.Current.IsIslandVisible;
        _loading = loading;
    }

    private void OnPositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var position = PositionBox.SelectedIndex switch
        {
            1 => IslandPosition.TopLeft,
            2 => IslandPosition.TopRight,
            3 => IslandPosition.BottomLeft,
            4 => IslandPosition.BottomCenter,
            5 => IslandPosition.BottomRight,
            _ => IslandPosition.TopCenter,
        };
        _settings.Update(s => s.Position = position);
    }

    private void OnDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DisplayBox.SelectedIndex >= 0)
            _settings.Update(s => s.DisplayIndex = DisplayBox.SelectedIndex - 1);
    }

    private void OnOffsetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue))
            return;
        _settings.Update(s =>
        {
            if (!double.IsNaN(OffsetXBox.Value)) s.OffsetX = OffsetXBox.Value;
            if (!double.IsNaN(OffsetYBox.Value)) s.OffsetY = OffsetYBox.Value;
        });
    }

    private void OnCollapsedStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && CollapsedStyleBox.SelectedIndex >= 0)
            _settings.Update(s => s.CollapsedStyle = (CollapsedStyle)CollapsedStyleBox.SelectedIndex);
    }

    private static readonly (string Name, string Hex)[] SolidColors =
    [
        ("曜石黑", "#FF000000"),
        ("深空灰", "#FF1E1F24"),
        ("午夜蓝", "#FF101B33"),
        ("松石绿", "#FF0F2A2A"),
        ("勃艮第", "#FF2E1018"),
        ("暮光紫", "#FF221634"),
    ];

    private void OnMaterialChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MaterialBox.SelectedIndex < 0)
            return;
        var material = (IslandMaterial)MaterialBox.SelectedIndex;
        SolidColorCard.Visibility = material == IslandMaterial.Solid ? Visibility.Visible : Visibility.Collapsed;
        _settings.Update(s => s.Material = material);
    }

    private void OnSolidColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SolidColorBox.SelectedIndex >= 0)
            _settings.Update(s => s.SolidColor = SolidColors[SolidColorBox.SelectedIndex].Hex);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
            return;
        UIElement[] pages = [Page0, Page1, Page2, Page3, Page4, Page5, Page6];
        for (int n = 0; n < pages.Length; n++)
            pages[n].Visibility = tag == n.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnGamePresentationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && GamePresentationBox.SelectedIndex >= 0)
            _settings.Update(s => s.GamePresentation = (GamePresentation)GamePresentationBox.SelectedIndex);
    }

    private void OnSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SourceBox.SelectedIndex >= 0)
            _settings.Update(s => s.Source = (PlayerSource)SourceBox.SelectedIndex);
    }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        var slider = (Slider)sender;
        _settings.Update(s =>
        {
            if (slider == CollapsedWidthSlider) s.CollapsedWidth = e.NewValue;
            else if (slider == ExpandedWidthSlider) s.ExpandedWidth = e.NewValue;
            else if (slider == DimSlider) s.BackdropDim = e.NewValue / 100;
            else if (slider == CornerRadiusSlider) s.CornerRadius = e.NewValue;
            else if (slider == VolumeStepSlider) s.VolumeStep = e.NewValue / 100;
            else if (slider == ScaleSlider) s.Scale = e.NewValue / 100;
            else if (slider == HoverDelaySlider) s.HoverExpandDelayMs = (int)e.NewValue;
            else if (slider == CollapseDelaySlider) s.CollapseDelayMs = (int)e.NewValue;
            else if (slider == LyricOffsetSlider) s.LyricOffsetMs = (int)e.NewValue;
        });
    }

    private void OnToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var toggle = (ToggleSwitch)sender;
        if (toggle == StartupToggle)
        {
            StartupService.SetEnabled(StartupToggle.IsOn);
            return;
        }
        _settings.Update(s =>
        {
            if (toggle == VisualizerToggle) s.ShowVisualizer = VisualizerToggle.IsOn;
            else if (toggle == DropletToggle) s.DropletEffect = DropletToggle.IsOn;
            else if (toggle == FlowToggle) s.LyricFlowAroundCover = FlowToggle.IsOn;
            else if (toggle == SweepToggle) s.WordSweep = SweepToggle.IsOn;
            else if (toggle == PeekToggle) s.PeekOnTrackChange = PeekToggle.IsOn;
            else if (toggle == ClickLyricsToggle) s.ClickToShowLyrics = ClickLyricsToggle.IsOn;
            else if (toggle == HideIdleToggle) s.HideWhenIdle = HideIdleToggle.IsOn;
            else if (toggle == AutoUpdateToggle) s.AutoCheckUpdates = AutoUpdateToggle.IsOn;
            else if (toggle == HidePanelToggle) s.IsIslandVisible = !HidePanelToggle.IsOn;
            else if (toggle == TopmostToggle) s.TopmostGuard = TopmostToggle.IsOn;
            else if (toggle == YieldFullscreenToggle) s.YieldToExclusiveFullscreen = YieldFullscreenToggle.IsOn;
            else if (toggle == FitLyricsToggle) s.FitCollapsedToLyrics = FitLyricsToggle.IsOn;
            else if (toggle == TwoLineToggle) s.CollapsedTwoLineLyrics = TwoLineToggle.IsOn;
            else if (toggle == LyricsToggle) s.LyricsEnabled = LyricsToggle.IsOn;
            else if (toggle == TranslationToggle) s.ShowTranslation = TranslationToggle.IsOn;
        });
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        _lyrics.ClearCache();
        LyricsActionStatus.Text = "已清除在线缓存；本地歌词和单曲校准已保留";
    }

    // ---------------------------------------------------------------- hotkeys

    private void BuildHotkeyRows()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = HotkeyFormatter.ActionName(action), FontSize = 14 });
            var warning = new TextBlock
            {
                Text = "与其他程序冲突，未能注册",
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 80, 80)),
                Visibility = Visibility.Collapsed,
            };
            labels.Children.Add(warning);
            grid.Children.Add(labels);

            var button = new Button { MinWidth = 180, VerticalAlignment = VerticalAlignment.Center, Tag = action };
            button.Click += (_, _) => StartRecording(action);
            button.PreviewKeyDown += OnHotkeyKeyDown;
            button.LostFocus += (_, _) => StopRecording();
            Grid.SetColumn(button, 1);
            grid.Children.Add(button);

            var clear = new Button
            {
                Content = new FontIcon { Glyph = char.ConvertFromUtf32(0xE711), FontSize = 12 },
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(clear, "清除");
            clear.Click += (_, _) => _settings.Update(s => s.Hotkeys[action] = null);
            Grid.SetColumn(clear, 2);
            grid.Children.Add(clear);

            HotkeyList.Children.Add(new Border { Style = (Style)((FrameworkElement)Content).Resources["CardStyle"], Child = grid });
            _hotkeyRows[action] = (button, warning);
        }
        _settings.Changed += UpdateHotkeyRows;
        Closed += (_, _) => _settings.Changed -= UpdateHotkeyRows;
    }

    private void UpdateHotkeyRows()
    {
        foreach (var (action, (button, warning)) in _hotkeyRows)
        {
            if (_recording != action)
                button.Content = HotkeyFormatter.Format(_settings.Current.Hotkeys.GetValueOrDefault(action));
            warning.Visibility = _hotkeys.Conflicts.Contains(action) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void StartRecording(HotkeyAction action)
    {
        StopRecording();
        _recording = action;
        _hotkeys.Suspend(); // otherwise the current combination would fire instead of being captured
        _hotkeyRows[action].Button.Content = "请按下组合键…";
    }

    private void StopRecording()
    {
        if (_recording is null)
            return;
        _recording = null;
        _hotkeys.Resume();
        UpdateHotkeyRows();
    }

    private void OnHotkeyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recording is not { } action || ((Button)sender).Tag is not HotkeyAction owner || owner != action)
            return;
        e.Handled = true;

        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }
        if (key is VirtualKey.Back or VirtualKey.Delete)
        {
            _settings.Update(s => s.Hotkeys[action] = null);
            StopRecording();
            return;
        }
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftShift or VirtualKey.RightShift)
            return;

        uint modifiers = 0;
        if (IsDown(VirtualKey.Control)) modifiers |= Native.MOD_CONTROL;
        if (IsDown(VirtualKey.Menu)) modifiers |= Native.MOD_ALT;
        if (IsDown(VirtualKey.Shift)) modifiers |= Native.MOD_SHIFT;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= Native.MOD_WIN;

        // Bare keys are only allowed for F-keys and media keys, otherwise normal typing would be hijacked.
        bool standalone = key is >= VirtualKey.F1 and <= VirtualKey.F24 || (int)key is >= 0xB0 and <= 0xB3;
        if (modifiers == 0 && !standalone)
            return;

        var binding = new HotkeyBinding(modifiers, (uint)key);
        _settings.Update(s =>
        {
            // A combination can only belong to one action.
            foreach (var other in s.Hotkeys.Where(p => p.Key != action && p.Value == binding).Select(p => p.Key).ToList())
                s.Hotkeys[other] = null;
            s.Hotkeys[action] = binding;
        });
        StopRecording();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
