using System.Runtime.InteropServices;

namespace LaLaLand.Interop;

/// <summary>
/// Hidden native window that receives tray-icon callbacks and WM_HOTKEY messages.
/// Lives on the UI thread, whose message pump is driven by WinUI.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    public const uint TrayCallbackMessage = Native.WM_APP + 1;
    private const string ClassName = "LaLaLand.MessageWindow";

    private readonly Native.WndProc _wndProc; // keep the delegate alive for the lifetime of the window
    private readonly uint _taskbarCreatedMessage;

    public IntPtr Handle { get; }

    /// <summary>Raised for every message; return a value to mark it handled.</summary>
    public event Func<uint, IntPtr, IntPtr, IntPtr?>? MessageReceived;

    public event Action? TaskbarCreated;

    public MessageWindow()
    {
        _wndProc = WndProc;
        _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);

        // A real (never shown) top-level window, not HWND_MESSAGE: message-only windows miss the TaskbarCreated broadcast.
        Handle = Native.CreateWindowEx(0, ClassName, "LaLaLand", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            TaskbarCreated?.Invoke();
            return IntPtr.Zero;
        }

        if (MessageReceived is { } handler)
        {
            foreach (Func<uint, IntPtr, IntPtr, IntPtr?> h in handler.GetInvocationList())
            {
                if (h(msg, wParam, lParam) is { } result)
                    return result;
            }
        }

        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            Native.DestroyWindow(Handle);
    }
}
