using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Input;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Keeps gameplay on an English keyboard layout while allowing text fields from this mod,
/// MiaoNet, CelesteNet, and other Everest mods to use an installed Chinese layout.
/// </summary>
internal static class WindowsInputLanguage {
    private const ushort SimplifiedChinese = 0x0804;
    private const ushort SingaporeChinese = 0x1004;
    private const ushort UnitedStatesEnglish = 0x0409;
    private const ushort PrimaryChinese = 0x04;
    private const ushort PrimaryEnglish = 0x09;

    private static IntPtr chineseLayout;
    private static IntPtr englishLayout;
    private static bool enabledLastFrame;
    private static bool warnedMissingChinese;
    private static bool warnedMissingEnglish;
    private static bool warnedSwitchFailure;
    private static bool warnedTextInputInspection;
    private static FieldInfo? textInputHandlersField;
    private static MethodInfo? imGuiGetIoMethod;
    private static PropertyInfo? imGuiWantTextInputProperty;

    public static void Load() {
        if (!OperatingSystem.IsWindows()) return;
        textInputHandlersField = typeof(TextInput).GetField("_OnInput", BindingFlags.NonPublic | BindingFlags.Static);
        RefreshLayouts();
    }

    public static void Unload() {
        chineseLayout = IntPtr.Zero;
        englishLayout = IntPtr.Zero;
        enabledLastFrame = false;
        warnedMissingChinese = false;
        warnedMissingEnglish = false;
        warnedSwitchFailure = false;
        warnedTextInputInspection = false;
        textInputHandlersField = null;
        imGuiGetIoMethod = null;
        imGuiWantTextInputProperty = null;
    }

    public static void Update() {
        if (!OperatingSystem.IsWindows()) return;

        bool enabled = MicroblocksQolUtilsModule.Settings.AutoSwitchInputLanguage;
        if (!enabled) {
            // Turning the option off while an editor is open must not leave gameplay stuck on
            // the Chinese layout selected by the previous frame.
            if (enabledLastFrame && WindowsNotifier.IsGameForeground() && englishLayout != IntPtr.Zero)
                SwitchLayout(englishLayout);
            enabledLastFrame = false;
            return;
        }

        if (!enabledLastFrame) {
            RefreshLayouts();
            enabledLastFrame = true;
        }

        // Keyboard layouts belong to a window/thread. Do not alter the game's layout while the
        // player is typing in another application, even if Celeste still has a text field open.
        if (!WindowsNotifier.IsGameForeground()) return;

        bool textEntryActive = IsTextEntryActive();

        IntPtr target = textEntryActive ? chineseLayout : englishLayout;
        if (target == IntPtr.Zero) {
            WarnMissingLayout(textEntryActive);
            return;
        }

        SwitchLayout(target);
    }

    private static bool IsTextEntryActive() {
        try {
            // TextInputEXT can stay active for the entire game when ImGuiHelper is installed:
            // that mod owns a permanent TextInput.OnInput subscription. Inspect Everest's actual
            // subscribers instead, ignoring that background bridge unless an ImGui text widget
            // is currently asking for OS text input.
            if (textInputHandlersField?.GetValue(null) is not Delegate handlers) return false;

            bool hasImGuiBridge = false;
            foreach (Delegate handler in handlers.GetInvocationList()) {
                if (handler.Method.Module.Assembly.GetName().Name == "ImGuiHelper") {
                    hasImGuiBridge = true;
                    continue;
                }
                return true;
            }
            return hasImGuiBridge && IsImGuiTextInputActive();
        } catch (Exception exception) {
            if (!warnedTextInputInspection) {
                warnedTextInputInspection = true;
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/InputLanguage",
                    $"Cannot inspect active text input subscribers: {exception.Message}");
            }
            return false;
        }
    }

    private static bool IsImGuiTextInputActive() {
        try {
            if (imGuiGetIoMethod is null || imGuiWantTextInputProperty is null) {
                Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == "ImGui.NET");
                Type? imGui = assembly?.GetType("ImGuiNET.ImGui", throwOnError: false);
                imGuiGetIoMethod = imGui?.GetMethod("GetIO", BindingFlags.Public | BindingFlags.Static,
                    binder: null, Type.EmptyTypes, modifiers: null);
                imGuiWantTextInputProperty = imGuiGetIoMethod?.ReturnType.GetProperty("WantTextInput");
            }

            object? io = imGuiGetIoMethod?.Invoke(null, null);
            return io is not null && imGuiWantTextInputProperty?.GetValue(io) is true;
        } catch {
            // ImGui may not have created a context yet. Its permanent subscriber should still not
            // force the whole game into the Chinese layout.
            return false;
        }
    }

    private static void SwitchLayout(IntPtr target) {
        if (GetKeyboardLayout(0) == target) return;
        if (ActivateKeyboardLayout(target, 0) == IntPtr.Zero && !warnedSwitchFailure) {
            warnedSwitchFailure = true;
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/InputLanguage",
                "Windows rejected the requested keyboard layout switch");
        }
    }

    private static void RefreshLayouts() {
        chineseLayout = IntPtr.Zero;
        englishLayout = IntPtr.Zero;
        warnedMissingChinese = false;
        warnedMissingEnglish = false;
        warnedSwitchFailure = false;
        warnedTextInputInspection = false;

        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return;

        IntPtr[] layouts = new IntPtr[count];
        count = Math.Min(count, GetKeyboardLayoutList(layouts.Length, layouts));
        if (count <= 0) return;

        ReadOnlySpan<IntPtr> installed = layouts.AsSpan(0, count);
        chineseLayout = FindLayout(installed, SimplifiedChinese)
            ?? FindLayout(installed, SingaporeChinese)
            ?? FindPrimaryLanguage(installed, PrimaryChinese)
            ?? IntPtr.Zero;
        englishLayout = FindLayout(installed, UnitedStatesEnglish)
            ?? FindPrimaryLanguage(installed, PrimaryEnglish)
            ?? IntPtr.Zero;

        Logger.Log(LogLevel.Verbose, "MicroblocksQolUtils/InputLanguage",
            $"Detected Chinese layout 0x{chineseLayout.ToInt64():X} and English layout 0x{englishLayout.ToInt64():X}");
    }

    private static IntPtr? FindLayout(ReadOnlySpan<IntPtr> layouts, ushort languageId) {
        foreach (IntPtr layout in layouts)
            if (LanguageId(layout) == languageId) return layout;
        return null;
    }

    private static IntPtr? FindPrimaryLanguage(ReadOnlySpan<IntPtr> layouts, ushort primaryLanguage) {
        foreach (IntPtr layout in layouts)
            if ((LanguageId(layout) & 0x03FF) == primaryLanguage) return layout;
        return null;
    }

    private static ushort LanguageId(IntPtr layout) => unchecked((ushort)layout.ToInt64());

    private static void WarnMissingLayout(bool chinese) {
        if (chinese) {
            if (warnedMissingChinese) return;
            warnedMissingChinese = true;
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/InputLanguage",
                "Automatic input-language switching is enabled, but no Chinese keyboard layout is installed");
        } else {
            if (warnedMissingEnglish) return;
            warnedMissingEnglish = true;
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/InputLanguage",
                "Automatic input-language switching is enabled, but no English keyboard layout is installed");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int bufferLength, [Out] IntPtr[]? layouts);

    [DllImport("user32.dll")]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
}
