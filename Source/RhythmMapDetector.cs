namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RhythmMapDetector {
    private static readonly string[] RhythmMarkers = [
        "cassetteblock",
        "rhythm",
        "musicsync",
        "syncedmusic",
        "beatblock",
        "tempoblock"
    ];

    public static bool IsRhythmSensitive(MapData? map) {
        if (map is null) return false;
        foreach (LevelData room in map.Levels) {
            if (room.Entities.Any(entity => IsRhythmMarker(entity.Name))
                || room.Triggers.Any(trigger => IsRhythmMarker(trigger.Name))) {
                return true;
            }
        }
        return false;
    }

    private static bool IsRhythmMarker(string name) {
        string normalized = name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return RhythmMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }
}
