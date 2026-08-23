using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class QolCommands {
    [Command("qol_watch", "Add a MiaoNet player to the watched list")]
    public static void Watch(string playerName) {
        if (WatchList.Add(playerName)) Engine.Commands.Log($"Watching {playerName}");
        else Engine.Commands.Log($"Already watching {playerName}");
    }

    [Command("qol_unwatch", "Remove a MiaoNet player from the watched list")]
    public static void Unwatch(string playerName) {
        if (WatchList.Remove(playerName)) Engine.Commands.Log($"Stopped watching {playerName}");
        else Engine.Commands.Log($"Not watched: {playerName}");
    }

    [Command("qol_watch_list", "List watched MiaoNet players")]
    public static void List() => Engine.Commands.Log(WatchList.Describe());
}

public static class WatchList {
    public static bool Contains(string name) => MicroblocksQolUtilsModule.Settings.WatchedPlayers
        .Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    public static bool Add(string name) {
        name = name.Trim();
        if (name.Length == 0 || Contains(name)) return false;
        MicroblocksQolUtilsModule.Settings.WatchedPlayers.Add(name);
        Save();
        return true;
    }

    public static bool Remove(string name) {
        int removed = MicroblocksQolUtilsModule.Settings.WatchedPlayers.RemoveAll(
            item => string.Equals(item, name.Trim(), StringComparison.OrdinalIgnoreCase)
        );
        if (removed > 0) Save();
        return removed > 0;
    }

    public static string Describe() => MicroblocksQolUtilsModule.Settings.WatchedPlayers.Count == 0
        ? "No watched players."
        : "Watched: " + string.Join(", ", MicroblocksQolUtilsModule.Settings.WatchedPlayers);

    private static void Save() => MicroblocksQolUtilsModule.Instance.SaveSettings();
}
