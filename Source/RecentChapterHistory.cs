namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RecentChapterHistory {
    private const int MaximumEntries = 16;

    public static IReadOnlyList<string> Entries {
        get {
            if (MicroblocksQolUtilsModule.Instance._SaveData is not QolSaveData data)
                return Array.Empty<string>();
            return data.RecentlyPlayedSids ??= [];
        }
    }

    public static void Record(string? sid) {
        if (string.IsNullOrWhiteSpace(sid)
            || MicroblocksQolUtilsModule.Instance._SaveData is not QolSaveData data) return;

        List<string> entries = data.RecentlyPlayedSids ??= [];
        entries.RemoveAll(entry => string.IsNullOrWhiteSpace(entry)
            || string.Equals(entry, sid, StringComparison.Ordinal));
        entries.Insert(0, sid);
        if (entries.Count > MaximumEntries)
            entries.RemoveRange(MaximumEntries, entries.Count - MaximumEntries);
    }
}
