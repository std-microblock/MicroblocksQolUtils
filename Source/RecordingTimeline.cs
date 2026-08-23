namespace Celeste.Mod.MicroblocksQolUtils;

public sealed record RecordingClip(
    string Source,
    double StartSeconds,
    double DurationSeconds,
    string MusicEvent,
    int MusicTimelineMilliseconds
);

public sealed record RecordingTimelineSnapshot(
    IReadOnlyList<RecordingClip> Clips,
    IReadOnlyList<RecordingClip>? RespawnAnchorClips = null
) {
    public RecordingTimelineSnapshot Copy() => new(
        Clips.ToArray(),
        RespawnAnchorClips?.ToArray()
    );
}
