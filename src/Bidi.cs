namespace Palon;

/// <summary>
/// Minimal bidi hygiene for composed display strings. Hebrew is the
/// product's default language but the UI is laid out LTR — a Hebrew run
/// glued to an LTR prefix ("050-1234567 · 3d ago: ") makes the neutral
/// separators reorder under the Unicode bidi algorithm. Wrapping the RTL
/// run in first-strong isolates pins every character where the format
/// string put it.
/// </summary>
static class Bidi
{
    const char Fsi = '\u2068'; // FIRST STRONG ISOLATE
    const char Pdi = '\u2069'; // POP DIRECTIONAL ISOLATE

    /// <summary>True when the text contains a Hebrew or Arabic code point
    /// (base blocks or presentation forms).</summary>
    public static bool HasRtl(string text)
    {
        foreach (var c in text)
        {
            if (c is (>= '֐' and <= 'ࣿ')      // Hebrew, Arabic + extensions
                or (>= 'יִ' and <= '﷿')        // presentation forms A
                or (>= 'ﹰ' and <= 'ﻼ')) return true; // presentation forms B
        }
        return false;
    }

    /// <summary>Wraps RTL-bearing text in isolates so it composes safely
    /// into an LTR line; pure-LTR text passes through untouched.</summary>
    public static string Isolate(string text) => HasRtl(text) ? Fsi + text + Pdi : text;
}
