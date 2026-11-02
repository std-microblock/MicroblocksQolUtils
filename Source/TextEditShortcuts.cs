using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Native text box shortcuts shared by every custom field: Ctrl+A/C/X/V. Clipboard access goes
/// through Everest's <see cref="TextInput"/> wrapper (SDL), so it works on Windows, Linux and
/// Android. Nothing here touches TextInputEXT's start/stop state, so an IME composition that is
/// in flight keeps running underneath the shortcut.
/// </summary>
internal static class TextEditShortcuts {
    /// <summary>Applies a Ctrl shortcut; returns true when one was consumed.</summary>
    public static bool Handle(TextEditBuffer buffer) {
        if (!MaterialTextInputFocus.ControlDown) return false;
        if (MaterialTextInputFocus.Pressed(Keys.A)) {
            buffer.SelectAll();
            return true;
        }
        if (MaterialTextInputFocus.Pressed(Keys.C)) return Copy(buffer);
        if (MaterialTextInputFocus.Pressed(Keys.X)) return Cut(buffer);
        if (MaterialTextInputFocus.Pressed(Keys.V)) return Paste(buffer);
        return false;
    }

    private static bool Copy(TextEditBuffer buffer) {
        if (!buffer.HasSelection) return false;
        try {
            TextInput.SetClipboardText(buffer.SelectedText);
            return true;
        } catch {
            return false;
        }
    }

    private static bool Cut(TextEditBuffer buffer) {
        if (!Copy(buffer)) return false;
        buffer.DeleteSelection();
        return true;
    }

    private static bool Paste(TextEditBuffer buffer) {
        string text;
        try {
            text = TextInput.GetClipboardText() ?? "";
        } catch {
            return false;
        }
        return buffer.InsertText(text);
    }
}
