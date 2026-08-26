using MonoMod.ModInterop;

#pragma warning disable CS0649

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class CollabUtils2Bridge {
    public static bool Available { get; private set; }

    public static void Load() {
        try {
            typeof(Imports).ModInterop();
            typeof(SavedStateImports).ModInterop();
            Available = Imports.IsCollabLobby is not null || Imports.IsCollabMap is not null;
            if (Available)
                Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "CollabUtils2 chapter-select integration enabled");
        } catch (Exception exception) {
            Available = false;
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/CollabUtils2", $"Interop unavailable: {exception.Message}");
        }
    }

    public static bool IsCollabLevelSet(string levelSet) => Imports.IsCollabLevelSet?.Invoke(levelSet) ?? false;
    public static bool IsCollabMap(string sid) => Imports.IsCollabMap?.Invoke(sid) ?? false;
    public static bool IsCollabLobby(string sid) => Imports.IsCollabLobby?.Invoke(sid) ?? false;
    public static bool IsCollabGym(string sid) => Imports.IsCollabGym?.Invoke(sid) ?? false;
    public static string? GetLobbyForMap(string sid) => Imports.GetLobbyForMap?.Invoke(sid);
    public static string? GetLobbyLevelSet(string sid) => Imports.GetLobbyLevelSet?.Invoke(sid);
    public static string? GetCollabName(string sid) => Imports.GetCollabNameForSID?.Invoke(sid);
    public static bool HasSavedState(string sid) => SavedStateImports.HasSavedState?.Invoke(sid) ?? false;

    public static bool ContinueSavedState(string sid) {
        if (SavedStateImports.TeleportToMapWithReturnToLobby is not { } teleport) return false;
        teleport(sid, null, 2, true, true);
        return true;
    }

    [ModImportName("CollabUtils2.LobbyHelper")]
    private static class Imports {
        public static Func<string, bool>? IsCollabLevelSet;
        public static Func<string, bool>? IsCollabMap;
        public static Func<string, bool>? IsCollabLobby;
        public static Func<string, bool>? IsCollabGym;
        public static Func<string, string?>? GetLobbyForMap;
        public static Func<string, string?>? GetLobbyLevelSet;
        public static Func<string, string?>? GetCollabNameForSID;
    }

    [ModImportName("CollabUtils2.ReturnToLobbyHelper")]
    private static class SavedStateImports {
        public static Func<string, bool>? HasSavedState;
        public static Action<string, string?, int, bool, bool>? TeleportToMapWithReturnToLobby;
    }
}
