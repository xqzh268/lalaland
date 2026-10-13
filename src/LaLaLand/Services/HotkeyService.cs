using LaLaLand.Interop;
using LaLaLand.Models;

namespace LaLaLand.Services;

/// <summary>System-wide hotkeys via RegisterHotKey. These keep working while a full-screen game has focus.</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly MessageWindow _window;
    private readonly SettingsService _settings;
    private readonly Dictionary<int, HotkeyAction> _registered = new();
    private string _registeredSignature = "";
    private bool _suspended;

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Actions whose hotkey could not be registered (usually taken by another app).</summary>
    public IReadOnlyCollection<HotkeyAction> Conflicts { get; private set; } = [];

    public event Action? ConflictsChanged;

    internal HotkeyService(MessageWindow window, SettingsService settings)
    {
        _window = window;
        _settings = settings;
        _window.MessageReceived += OnMessage;
        _settings.Changed += Apply;
        Apply();
    }

    /// <summary>Temporarily releases all hotkeys, e.g. while the user records a new combination.</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        _suspended = false;
        Apply();
    }

    private void Apply()
    {
        if (_suspended)
            return;

        var signature = string.Join(";", _settings.Current.Hotkeys.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value?.Modifiers}:{p.Value?.Key}"));
        if (signature == _registeredSignature)
            return;

        UnregisterAll();
        var conflicts = new List<HotkeyAction>();
        int id = 1;
        foreach (var (action, binding) in _settings.Current.Hotkeys)
        {
            if (binding is null)
                continue;
            // Volume keys auto-repeat while held; everything else fires once per press.
            uint repeat = action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown ? 0 : Native.MOD_NOREPEAT;
            if (Native.RegisterHotKey(_window.Handle, id, binding.Modifiers | repeat, binding.Key))
                _registered[id] = action;
            else
            {
                Log.Info($"RegisterHotKey failed for {action}: Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                conflicts.Add(action);
            }
            id++;
        }
        _registeredSignature = signature;
        Conflicts = conflicts;
        ConflictsChanged?.Invoke();
        if (conflicts.Count > 0)
            Log.Info("Hotkey conflicts: " + string.Join(", ", conflicts));
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            Native.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
        _registeredSignature = "";
    }

    private IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Native.WM_HOTKEY)
            return null;
        if (_registered.TryGetValue((int)wParam, out var action))
            Pressed?.Invoke(action);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _settings.Changed -= Apply;
        _window.MessageReceived -= OnMessage;
        UnregisterAll();
    }
}
