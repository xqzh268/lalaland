using System.Globalization;
using System.Runtime.InteropServices;

namespace LaLaLand.Services;

/// <summary>Installed font families, enumerated through GDI (no extra dependencies).</summary>
public static class FontCatalog
{
    private const byte DEFAULT_CHARSET = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet, lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUMLOGFONTEX
    {
        public LOGFONT elfLogFont;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string elfFullName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string elfStyle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string elfScript;
    }

    private delegate int EnumFontFamExProc(ref ENUMLOGFONTEX font, IntPtr metrics, uint fontType, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesEx(IntPtr hdc, ref LOGFONT logFont, EnumFontFamExProc proc, IntPtr lParam, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    /// <summary>Family names (localized names included, e.g. 微软雅黑), sorted, without vertical "@" variants.</summary>
    public static IReadOnlyList<string> GetFamilies()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var query = new LOGFONT { lfCharSet = DEFAULT_CHARSET, lfFaceName = "" };
            EnumFontFamExProc proc = (ref ENUMLOGFONTEX font, IntPtr _, uint _, IntPtr _) =>
            {
                var name = font.elfLogFont.lfFaceName;
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@'))
                    names.Add(name);
                return 1;
            };
            EnumFontFamiliesEx(hdc, ref query, proc, IntPtr.Zero, 0);
            GC.KeepAlive(proc);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
        return names.OrderBy(n => n, comparer).ToList();
    }
}
