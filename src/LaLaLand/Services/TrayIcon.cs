using System.Runtime.InteropServices;
using LaLaLand.Interop;

namespace LaLaLand.Services;

/// <summary>Notification-area icon with a native (dark-themed) context menu. No extra windows, no XAML.</summary>
internal sealed class TrayIcon : IDisposable
{
    public sealed record MenuItem(string Text, Action? OnClick = null, bool Checked = false, bool Radio = false, bool Enabled = true, IReadOnlyList<MenuItem>? Children = null)
    {
        public static readonly MenuItem Separator = new("-");
    }

    private const uint IconId = 1;
    private readonly MessageWindow _window;
    private readonly IntPtr _icon;
    private bool _added;

    public string Tooltip { get; private set; } = "LaLaLand";

    /// <summary>Builds the menu on demand so check marks always reflect current state.</summary>
    public Func<IReadOnlyList<MenuItem>>? MenuProvider { get; set; }

    public event Action? LeftClick;

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "LaLaLand-v2.ico");
        int size = Native.GetSystemMetrics(Native.SM_CXSMICON);
        _icon = Native.LoadImage(IntPtr.Zero, iconPath, Native.IMAGE_ICON, size, size, Native.LR_LOADFROMFILE);

        try
        {
            Native.SetPreferredAppMode(1); // AllowDark: follow the system theme for Win32 menus
            Native.FlushMenuThemes();
        }
        catch
        {
            // Older Windows builds without the ordinal exports
        }

        _window.MessageReceived += OnMessage;
        _window.TaskbarCreated += Add; // Explorer restarted
        Add();
    }

    public void SetTooltip(string text)
    {
        Tooltip = text.Length > 127 ? text[..124] + "..." : text;
        if (!_added)
            return;
        var data = CreateData(Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    private void Add()
    {
        var data = CreateData(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    private Native.NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = MessageWindow.TrayCallbackMessage,
        hIcon = _icon,
        szTip = Tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != MessageWindow.TrayCallbackMessage)
            return null;

        // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor point in screen coordinates
        switch ((uint)Native.LoWord(lParam))
        {
            case Native.WM_LBUTTONUP:
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                LeftClick?.Invoke();
                break;
            case Native.WM_CONTEXTMENU:
            case Native.WM_RBUTTONUP:
                ShowMenu(Native.LoWord(wParam), Native.HiWord(wParam));
                break;
        }
        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        if (MenuProvider is null)
            return;

        var actions = new List<Action?> { null }; // command ids start at 1
        var menu = Build(MenuProvider(), actions);
        try
        {
            if (x == 0 && y == 0 && Native.GetCursorPos(out var pt))
                (x, y) = (pt.X, pt.Y);

            // Required so the menu closes when the user clicks elsewhere.
            Native.SetForegroundWindow(_window.Handle);
            int command = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN | Native.TPM_RIGHTALIGN, x, y, _window.Handle, IntPtr.Zero);
            Native.PostMessage(_window.Handle, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command > 0 && command < actions.Count)
                actions[command]?.Invoke();
        }
        finally
        {
            Native.DestroyMenu(menu); // destroys submenus too
        }
    }

    private static IntPtr Build(IReadOnlyList<MenuItem> items, List<Action?> actions)
    {
        var menu = Native.CreatePopupMenu();
        foreach (var item in items)
        {
            if (ReferenceEquals(item, MenuItem.Separator))
            {
                Native.AppendMenu(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);
                continue;
            }

            uint flags = Native.MF_STRING;
            if (item.Checked) flags |= Native.MF_CHECKED;
            if (item.Radio) flags |= Native.MFT_RADIOCHECK;
            if (!item.Enabled) flags |= Native.MF_GRAYED;

            if (item.Children is { Count: > 0 })
            {
                Native.AppendMenu(menu, flags | Native.MF_POPUP, Build(item.Children, actions), item.Text);
            }
            else
            {
                actions.Add(item.OnClick);
                Native.AppendMenu(menu, flags, actions.Count - 1, item.Text);
            }
        }
        return menu;
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        _window.TaskbarCreated -= Add;
        if (_added)
        {
            var data = CreateData(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }
        if (_icon != IntPtr.Zero)
            Native.DestroyIcon(_icon);
    }
}
