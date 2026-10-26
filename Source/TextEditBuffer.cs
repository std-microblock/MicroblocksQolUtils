using System.Text;
using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Editable text with a caret, used by both the inline setting editor and the in-game
/// keyboard prompt. Spaces are rendered as a visible dot so leading/trailing spaces can be
/// inspected, and every width is measured through the TTF font (CJK glyphs count double).
/// </summary>
internal sealed class TextEditBuffer {
    private const char SpaceMarker = '·';

    public string Text { get; private set; }
    public int Cursor { get; private set; }
    public int MaxLength { get; }

    public TextEditBuffer(string value, int maxLength) {
        Text = value ?? "";
        MaxLength = Math.Max(1, maxLength);
        Cursor = Text.Length;
    }

    public string Display => Text.Replace(' ', SpaceMarker);

    public bool Insert(char character) {
        if (Text.Length >= MaxLength) return false;
        Text = Text.Insert(Cursor, character.ToString());
        Cursor++;
        return true;
    }

    public bool Backspace() {
        if (Cursor <= 0) return false;
        Text = Text.Remove(Cursor - 1, 1);
        Cursor--;
        return true;
    }

    public bool Delete() {
        if (Cursor >= Text.Length) return false;
        Text = Text.Remove(Cursor, 1);
        return true;
    }

    public void MoveLeft() => Cursor = Math.Max(0, Cursor - 1);

    public void MoveRight() => Cursor = Math.Min(Text.Length, Cursor + 1);

    public void MoveHome() => Cursor = 0;

    public void MoveEnd() => Cursor = Text.Length;

    public void SetCursor(int index) => Cursor = Math.Clamp(index, 0, Text.Length);

    public float CaretWidth(float scale, UiFontWeight weight) =>
        SystemTtfFont.MeasureVisible(Display[..Cursor], scale, weight).X;

    /// <summary>
    /// Window of the display text around the caret, plus the index (in <see cref="Display"/>)
    /// where the window starts and the caret offset inside it. Used to draw fields that are
    /// narrower than the value without the caret drifting off-screen.
    /// </summary>
    public TextWindow Window(float maxWidth, float scale, UiFontWeight weight) {
        string display = Display;
        int start = 0;
        while (start < Cursor
            && SystemTtfFont.MeasureVisible(display[start..Cursor], scale, weight).X > maxWidth - 10f) {
            start++;
        }
        string shown = start >= display.Length ? "" : display[start..];
        if (SystemTtfFont.MeasureVisible(shown, scale, weight).X > maxWidth) {
            shown = MaterialTextUtil.Ellipsize(shown, maxWidth, scale, weight);
        }
        return new TextWindow(shown, start, Math.Clamp(Cursor - start, 0, shown.Length));
    }

    /// <summary>Maps a local x offset inside a <see cref="TextWindow"/> to a caret index.</summary>
    public void SetCursorFromX(float localX, TextWindow window, float scale, UiFontWeight weight) {
        string display = Display;
        int end = window.Start + window.Text.Length;
        int best = window.Start;
        float bestDistance = float.MaxValue;
        for (int index = window.Start; index <= Math.Min(end, display.Length); index++) {
            float width = SystemTtfFont.MeasureVisible(display[window.Start..index], scale, weight).X;
            float distance = Math.Abs(width - localX);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = index;
        }
        Cursor = Math.Clamp(best, 0, Text.Length);
    }

    public readonly record struct TextWindow(string Text, int Start, int CaretOffset);

    public static string Visible(string value) => value.Replace(' ', SpaceMarker);
}
