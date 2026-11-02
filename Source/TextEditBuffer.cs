using System.Text;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Editable single-line text with a caret and a selection anchor. The value is kept verbatim
/// (spaces stay spaces); width measurement and the visible window live in
/// <see cref="TextEditGeometry"/> so this type can be exercised without the game assemblies.
/// </summary>
internal sealed class TextEditBuffer {
    public string Text { get; private set; }
    public int Cursor { get; private set; }
    public int Anchor { get; private set; }
    public int MaxLength { get; }

    public TextEditBuffer(string value, int maxLength) {
        Text = value ?? "";
        MaxLength = Math.Max(1, maxLength);
        Cursor = Text.Length;
        Anchor = Cursor;
    }

    public int SelectionStart => Math.Min(Cursor, Anchor);
    public int SelectionLength => Math.Abs(Cursor - Anchor);
    public bool HasSelection => Cursor != Anchor;
    public string SelectedText => Text.Substring(SelectionStart, SelectionLength);

    public void SetText(string? value) {
        string text = value ?? "";
        Text = text.Length > MaxLength ? text[..MaxLength] : text;
        Cursor = Text.Length;
        Anchor = Cursor;
    }

    public bool Insert(char character) => InsertText(character.ToString());

    /// <summary>
    /// Inserts <paramref name="value"/> at the caret, replacing the selection. Clipboard input
    /// can carry line breaks and tabs, so control characters are dropped first: this is a
    /// single-line field. Returns true when the value actually changed.
    /// </summary>
    public bool InsertText(string? value) {
        value = SingleLine(value);
        if (value.Length == 0) return false;
        int room = MaxLength - (Text.Length - SelectionLength);
        if (room <= 0) return false;
        if (value.Length > room) value = value[..room];
        int start = SelectionStart;
        Text = string.Concat(Text.AsSpan(0, start), value, Text.AsSpan(start + SelectionLength));
        Cursor = start + value.Length;
        Anchor = Cursor;
        return true;
    }

    /// <summary>Strips characters that a single-line field cannot show (CR/LF/TAB and friends).</summary>
    public static string SingleLine(string? value) {
        if (string.IsNullOrEmpty(value)) return "";
        StringBuilder builder = new(value.Length);
        foreach (char character in value) {
            if (!char.IsControl(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    public bool Backspace() {
        if (HasSelection) return DeleteSelection();
        if (Cursor <= 0) return false;
        Text = Text.Remove(Cursor - 1, 1);
        Cursor--;
        Anchor = Cursor;
        return true;
    }

    public bool Delete() {
        if (HasSelection) return DeleteSelection();
        if (Cursor >= Text.Length) return false;
        Text = Text.Remove(Cursor, 1);
        return true;
    }

    /// <summary>Removes the selected range; the caret ends up where the selection started.</summary>
    public bool DeleteSelection() {
        if (!HasSelection) return false;
        int start = SelectionStart;
        Text = Text.Remove(start, SelectionLength);
        Cursor = start;
        Anchor = start;
        return true;
    }

    public void SelectAll() {
        Anchor = 0;
        Cursor = Text.Length;
    }

    public void CollapseSelection() {
        Cursor = SelectionStart;
        Anchor = Cursor;
    }

    public void MoveLeft(bool extend = false) {
        if (!extend && HasSelection) {
            Cursor = SelectionStart;
            Anchor = Cursor;
            return;
        }
        MoveTo(Cursor - 1, extend);
    }

    public void MoveRight(bool extend = false) {
        if (!extend && HasSelection) {
            Cursor = SelectionStart + SelectionLength;
            Anchor = Cursor;
            return;
        }
        MoveTo(Cursor + 1, extend);
    }

    public void MoveHome(bool extend = false) => MoveTo(0, extend);

    public void MoveEnd(bool extend = false) => MoveTo(Text.Length, extend);

    public void SetCursor(int index, bool extend = false) => MoveTo(index, extend);

    private void MoveTo(int index, bool extend) {
        index = Math.Clamp(index, 0, Text.Length);
        if (extend && !HasSelection) Anchor = Cursor;
        Cursor = index;
        if (!extend) Anchor = index;
    }
}
