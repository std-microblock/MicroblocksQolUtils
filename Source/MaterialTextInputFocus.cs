using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Owns keyboard focus for the custom material text fields and hides their keystrokes from
/// Celeste's shortcut/input layer while they are being edited.
/// </summary>
internal static class MaterialTextInputFocus {
    private static object? focusedOwner;
    private static bool suppressKeyboard;
    private static KeyboardState rawPreviousState;
    private static KeyboardState rawCurrentState;

    public static void Load() {
        On.Monocle.MInput.KeyboardData.Update += KeyboardUpdate;
        On.Monocle.MInput.KeyboardData.Check_Keys += KeyboardCheck;
        On.Monocle.MInput.KeyboardData.Pressed_Keys += KeyboardPressed;
        On.Monocle.MInput.KeyboardData.Released_Keys += KeyboardReleased;
    }

    public static void Unload() {
        On.Monocle.MInput.KeyboardData.Released_Keys -= KeyboardReleased;
        On.Monocle.MInput.KeyboardData.Pressed_Keys -= KeyboardPressed;
        On.Monocle.MInput.KeyboardData.Check_Keys -= KeyboardCheck;
        On.Monocle.MInput.KeyboardData.Update -= KeyboardUpdate;
        focusedOwner = null;
        suppressKeyboard = false;
        rawPreviousState = default;
        rawCurrentState = default;
    }

    /// <summary>Samples the physical keyboard before MInput replaces it with the suppressed state.</summary>
    public static void BeginFrame() {
        rawPreviousState = rawCurrentState;
        rawCurrentState = Keyboard.GetState();

        // Keep swallowing the key that closed the editor until it has been released. This prevents
        // Enter/Escape (or a captured hotkey) from leaking to another mod as focus is handed back.
        if (focusedOwner is null && suppressKeyboard && rawCurrentState.GetPressedKeys().Length == 0)
            suppressKeyboard = false;
    }

    public static void Focus(object owner) {
        focusedOwner = owner;
        suppressKeyboard = true;

        // Focus can be acquired after MInput.Update has already run for this frame. Clear its public
        // snapshot immediately so later entities cannot observe the click-to-focus frame's keys.
        if (MInput.Keyboard is not null) {
            MInput.Keyboard.PreviousState = default;
            MInput.Keyboard.CurrentState = default;
        }
    }

    public static void Blur(object owner) {
        if (ReferenceEquals(focusedOwner, owner)) focusedOwner = null;
    }

    public static bool Pressed(Keys key) => key != Keys.None
        && rawCurrentState.IsKeyDown(key)
        && rawPreviousState.IsKeyUp(key);

    public static Keys[] GetPressedKeys() => rawCurrentState.GetPressedKeys();

    private static void KeyboardUpdate(
        On.Monocle.MInput.KeyboardData.orig_Update orig,
        MInput.KeyboardData self
    ) {
        orig(self);
        if (!suppressKeyboard) return;
        self.PreviousState = default;
        self.CurrentState = default;
    }

    private static bool KeyboardCheck(
        On.Monocle.MInput.KeyboardData.orig_Check_Keys orig,
        MInput.KeyboardData self,
        Keys key
    ) => !suppressKeyboard && orig(self, key);

    private static bool KeyboardPressed(
        On.Monocle.MInput.KeyboardData.orig_Pressed_Keys orig,
        MInput.KeyboardData self,
        Keys key
    ) => !suppressKeyboard && orig(self, key);

    private static bool KeyboardReleased(
        On.Monocle.MInput.KeyboardData.orig_Released_Keys orig,
        MInput.KeyboardData self,
        Keys key
    ) => !suppressKeyboard && orig(self, key);
}
