using System.Globalization;
using System.Text;

namespace LaLaLand.Models;

/// <summary>Splits by Unicode scalar, preserving source text and UTF-16 indices used by lyric timing.</summary>
internal static class LyricFontScripts
{
    internal readonly record struct FontRun(string Text, bool EastAsian);

    public static IEnumerable<FontRun> Split(string text)
    {
        int start = 0, offset = 0;
        bool current = false, initialized = false;
        foreach (var rune in text.EnumerateRunes())
        {
            bool eastAsian = IsEastAsian(rune.Value);
            // Combining marks and variation selectors must stay with the character they modify.
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                eastAsian = initialized && current;
            if (initialized && eastAsian != current)
            {
                yield return new FontRun(text[start..offset], current);
                start = offset;
            }
            initialized = true;
            current = eastAsian;
            offset += rune.Utf16SequenceLength;
        }
        if (initialized) yield return new FontRun(text[start..], current);
    }

    private static bool IsEastAsian(int scalar) => scalar is
        >= 0x1100 and <= 0x11FF or // Hangul jamo
        >= 0x2E80 and <= 0x31FF or // radicals, CJK punctuation, kana, bopomofo, Hangul compatibility jamo
        >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
        >= 0xA960 and <= 0xA97F or >= 0xAC00 and <= 0xD7FF or
        >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or
        >= 0xFF00 and <= 0xFFEF or // full-width forms, half-width kana and Hangul
        >= 0x1AFF0 and <= 0x1B16F or // supplementary kana
        >= 0x20000 and <= 0x323AF; // supplementary Han, including compatibility ideographs
}
