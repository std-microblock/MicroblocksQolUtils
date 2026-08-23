using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class QolPauseMenu {
    public static void Load() => Everest.Events.Level.OnCreatePauseMenuButtons += AddPauseMenuButton;

    public static void Unload() => Everest.Events.Level.OnCreatePauseMenuButtons -= AddPauseMenuButton;

    private static void AddPauseMenuButton(Level level, TextMenu menu, bool minimal) {
        int optionsIndex = menu.Items.FindIndex(item =>
            item.GetType() == typeof(TextMenu.Button)
            && ((TextMenu.Button)item).Label == Dialog.Get("menu_pause_options")
        );
        if (optionsIndex < 0) return;

        TextMenu.Item item = new TextMenu.Button("Microblock 的 QOL 工具");
        item.Pressed(() => {
            int returnIndex = menu.IndexOf(item);
            menu.RemoveSelf();
            level.PauseMainMenuOpen = false;
            level.Paused = true;
            bool oldAllowHudHide = level.AllowHudHide;
            level.AllowHudHide = false;
            level.Add(new QolSettingsOverlay(level, returnIndex, minimal, oldAllowHudHide));
        });
        menu.Insert(optionsIndex + 1, item);
    }
}
