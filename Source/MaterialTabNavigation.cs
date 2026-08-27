using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Shared keyboard gesture for moving between tabs on material pages.
/// </summary>
internal static class MaterialTabNavigation {
    public static bool TryGetShiftVerticalDirection(out int direction) {
        direction = 0;
        if (!MInput.Keyboard.Check(Keys.LeftShift, Keys.RightShift)) return false;

        if (Input.MenuUp.Pressed) direction = -1;
        else if (Input.MenuDown.Pressed) direction = 1;
        return direction != 0;
    }
}
