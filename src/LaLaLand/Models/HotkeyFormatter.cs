using LaLaLand.Interop;

namespace LaLaLand.Models;

public static class HotkeyFormatter
{
    public static string Format(HotkeyBinding? binding)
    {
        if (binding is null)
            return "未设置";

        var parts = new List<string>(5);
        if ((binding.Modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((binding.Modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((binding.Modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(binding.Key));
        return string.Join(" + ", parts);
    }

    public static string KeyName(uint vk) => vk switch
    {
        0x20 => "Space",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x0D => "Enter",
        0x09 => "Tab",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x2D => "Insert",
        0x2E => "Delete",
        0xB0 => "Media Next",
        0xB1 => "Media Prev",
        0xB3 => "Media Play",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => $"0x{vk:X2}",
    };

    public static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.PlayPause => "播放 / 暂停",
        HotkeyAction.Next => "下一首",
        HotkeyAction.Previous => "上一首",
        HotkeyAction.ToggleFavorite => "红心收藏",
        HotkeyAction.ToggleLyrics => "展开 / 收起歌词",
        HotkeyAction.ToggleIsland => "显示 / 隐藏面板",
        HotkeyAction.VolumeUp => "音量 +",
        HotkeyAction.VolumeDown => "音量 −",
        HotkeyAction.ToggleMute => "静音 / 取消静音",
        _ => action.ToString(),
    };
}
