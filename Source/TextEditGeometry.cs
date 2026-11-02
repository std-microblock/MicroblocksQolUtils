using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Width measurement for <see cref="TextEditBuffer"/>: every position comes from the TTF font,
/// so CJK/full-width glyphs count twice and the caret never drifts half a cell.
/// </summary>
internal static class TextEditGeometry {
    public static float CaretWidth(TextEditBuffer buffer, float scale, UiFontWeight weight) =>
        SystemTtfFont.MeasureVisible(buffer.Text[..buffer.Cursor], scale, weight).X;

    /// <summary>
    /// Window of the text around the caret, plus the index where the window starts and the
    /// caret offset inside it. Used to draw fields that are narrower than the value without
    /// the caret drifting off-screen.
    /// </summary>
    public static TextWindow Window(TextEditBuffer buffer, float maxWidth, float scale,
        UiFontWeight weight) {
        string text = buffer.Text;
        int start = 0;
        while (start < buffer.Cursor
            && SystemTtfFont.MeasureVisible(text[start..buffer.Cursor], scale, weight).X
            > maxWidth - 10f) {
            start++;
        }
        string shown = start >= text.Length ? "" : text[start..];
        if (SystemTtfFont.MeasureVisible(shown, scale, weight).X > maxWidth) {
            shown = MaterialTextUtil.Ellipsize(shown, maxWidth, scale, weight);
        }
        return new TextWindow(shown, start, Math.Clamp(buffer.Cursor - start, 0, shown.Length));
    }

    /// <summary>Maps a local x offset inside a <see cref="TextWindow"/> to a caret index.</summary>
    public static int IndexFromX(TextEditBuffer buffer, float localX, TextWindow window, float scale,
        UiFontWeight weight) {
        string text = buffer.Text;
        int end = window.Start + window.Text.Length;
        int best = window.Start;
        float bestDistance = float.MaxValue;
        for (int index = window.Start; index <= Math.Min(end, text.Length); index++) {
            float width = SystemTtfFont.MeasureVisible(text[window.Start..index], scale, weight).X;
            float distance = Math.Abs(width - localX);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        return Math.Clamp(best, 0, text.Length);
    }

    public readonly record struct TextWindow(string Text, int Start, int CaretOffset);
}
