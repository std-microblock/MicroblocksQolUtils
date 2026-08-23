using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class AutoRecorder {
    private const double MinimumClipSeconds = 0.02;
    private const int MusicTimelineDiscontinuityMilliseconds = 750;

    private static readonly List<RecordingClip> ActivePrefix = [];
    private static NativeRoomRecording? current;
    private static RecordingTimelineSnapshot? respawnAnchor;
    private static Vector2? observedRespawnPoint;
    private static MusicPosition branchMusicStart;
    private static double branchStartSeconds;
    private static string roomKey = "";
    private static string roomName = "";
    private static string areaSid = "";
    private static bool branchActive;
    private static bool waitingForStablePlayer;
    private static bool pauseSuspended;
    private static bool completing;
    private static bool manualMode;
    private static int finalizingCount;
    private static int cleanupRunning;
    private static string lastOutput = "";
    private static string lastCleanupStatus = "—";

    public static bool ManualMode => manualMode;
    public static bool IsRecording => current is not null;
    public static bool IsFinalizing => Volatile.Read(ref finalizingCount) > 0;
    public static bool IsCleaning => Volatile.Read(ref cleanupRunning) != 0;
    public static double CurrentSeconds => current?.MediaTimeSeconds ?? 0;
    public static string CurrentPath => current?.Path ?? "";
    public static string LastOutput => lastOutput;
    public static string LastCleanupStatus => lastCleanupStatus;
    public static string RecordingRoot => ResolveRecordingRoot();

    public static void Load(string directory) {
        _ = directory;
        On.Celeste.Player.Die += PlayerDie;
        On.Celeste.Level.TransitionTo += LevelTransitionTo;
        On.Celeste.Level.RegisterAreaComplete += RegisterAreaComplete;
        SpeedrunToolBridge.Load();
        CleanupRecordings();
    }

    public static void Unload() {
        manualMode = false;
        SpeedrunToolBridge.Unload();
        On.Celeste.Level.RegisterAreaComplete -= RegisterAreaComplete;
        On.Celeste.Level.TransitionTo -= LevelTransitionTo;
        On.Celeste.Player.Die -= PlayerDie;
        StopAndReset(deleteSource: true);
    }

    public static void Update(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if ((!settings.AutoRecorderEnabled && !manualMode) || !OperatingSystem.IsWindows()) {
            if (current is not null || roomKey.Length > 0) StopAndReset(deleteSource: true);
            return;
        }

        Player? player = level.Tracker.GetEntity<Player>();
        if (player is null) return;
        string key = RoomKey(level);
        if (!string.Equals(key, roomKey, StringComparison.Ordinal)) {
            if (roomKey.Length > 0 && !completing) StopAndReset(deleteSource: true);
            BeginRoom(level);
        }

        if (!manualMode && !ShouldRecord(player, settings)) {
            if (current is not null) DiscardCurrentRecording();
            return;
        }

        if (level.Paused) {
            SuspendForPause();
            return;
        }

        if (current is null && !player.Dead && !level.Transitioning) {
            StartRoomRecording(level);
        }
        NativeRoomRecording? recording = current;
        if (recording is null) return;

        if (pauseSuspended && !player.Dead && !level.Transitioning) {
            pauseSuspended = false;
            StartBranchAtCurrentTime();
        } else if (waitingForStablePlayer && !player.Dead && !level.Transitioning) {
            StartBranchAtCurrentTime();
        }

        if (branchActive && settings.BgmMode == BgmRecordingMode.SfxOnlyWithPostMix)
            ObserveMusicTimeline(recording);

        Vector2? respawn = level.Session.RespawnPoint;
        if (branchActive
            && observedRespawnPoint.HasValue
            && respawn.HasValue
            && Vector2.DistanceSquared(observedRespawnPoint.Value, respawn.Value) > 0.01f) {
            respawnAnchor = new RecordingTimelineSnapshot(CaptureCurrentClips(recording));
        }
        observedRespawnPoint = respawn;
    }

    public static void StartManual() {
        if (!OperatingSystem.IsWindows()) return;
        manualMode = true;
    }

    public static void StopManual(Level? level, bool save) {
        manualMode = false;
        if (current is null) return;
        if (save && level is not null) FinalizeCurrent(level);
        else DiscardCurrentRecording();
    }

    public static void CleanupRecordings() {
        int retentionCount = Math.Max(0, MicroblocksQolUtilsModule.Settings.RecordingRetentionCount);
        if (retentionCount == 0) {
            lastCleanupStatus = "未启用保留上限";
            return;
        }
        if (Interlocked.Exchange(ref cleanupRunning, 1) != 0) return;
        lastCleanupStatus = "清理中";
        _ = Task.Run(() => {
            try {
                int deleted = DeleteOldCompletedRecordings(ResolveRecordingRoot(), retentionCount);
                lastCleanupStatus = deleted == 0 ? "无需清理" : $"已清理 {deleted} 个";
            } catch (Exception exception) {
                lastCleanupStatus = "清理失败";
                Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder/Cleanup");
            } finally {
                Volatile.Write(ref cleanupRunning, 0);
            }
        });
    }

    public static RecordingTimelineSnapshot? CaptureTimeline(Level level) {
        NativeRoomRecording? recording = current;
        if (recording is null
            || !branchActive
            || !string.Equals(level.Session.Level, roomName, StringComparison.Ordinal)) {
            return null;
        }
        return new RecordingTimelineSnapshot(
            CaptureCurrentClips(recording),
            respawnAnchor?.Clips.ToArray()
        );
    }

    public static void RestoreTimeline(Level level, RecordingTimelineSnapshot snapshot) {
        NativeRoomRecording? recording = current;
        if (!MicroblocksQolUtilsModule.Settings.AutoRecorderEnabled || recording is null) return;
        if (!string.Equals(RoomKey(level), roomKey, StringComparison.Ordinal)) return;
        if (snapshot.Clips.Any(clip => !string.Equals(clip.Source, recording.Path, StringComparison.OrdinalIgnoreCase))) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", "Ignored SpeedrunTool timeline from another recording session.");
            return;
        }
        ActivePrefix.Clear();
        ActivePrefix.AddRange(snapshot.Clips);
        respawnAnchor = snapshot.RespawnAnchorClips is null
            ? null
            : new RecordingTimelineSnapshot(snapshot.RespawnAnchorClips.ToArray());
        branchActive = false;
        waitingForStablePlayer = true;
        pauseSuspended = false;
        observedRespawnPoint = level.Session.RespawnPoint;
    }

    private static PlayerDeadBody? PlayerDie(
        On.Celeste.Player.orig_Die orig,
        Player self,
        Vector2 direction,
        bool evenIfInvincible,
        bool registerDeathInStats
    ) {
        PlayerDeadBody? body = orig(self, direction, evenIfInvincible, registerDeathInStats);
        if (body is null || current is null) return body;
        ActivePrefix.Clear();
        if (respawnAnchor is not null) ActivePrefix.AddRange(respawnAnchor.Clips);
        branchActive = false;
        waitingForStablePlayer = true;
        pauseSuspended = false;
        return body;
    }

    private static void LevelTransitionTo(
        On.Celeste.Level.orig_TransitionTo orig,
        Level self,
        LevelData next,
        Vector2 direction
    ) {
        Complete(self);
        orig(self, next, direction);
    }

    private static void RegisterAreaComplete(On.Celeste.Level.orig_RegisterAreaComplete orig, Level self) {
        Complete(self);
        orig(self);
    }

    private static void BeginRoom(Level level) {
        completing = false;
        roomKey = RoomKey(level);
        roomName = level.Session.Level;
        areaSid = level.Session.Area.SID;
        observedRespawnPoint = level.Session.RespawnPoint;
        respawnAnchor = null;
        ActivePrefix.Clear();
        branchActive = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
    }

    private static void StartRoomRecording(Level level) {
        string tempRoot = Path.Combine(ResolveRecordingRoot(), ".working", Sanitize(roomKey));
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot, $"room-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.mkv");
        current = NativeRoomRecording.Start(path);
        if (current is null) return;
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = level.Session.RespawnPoint;
        StartBranchAtCurrentTime();
    }

    private static void StartBranchAtCurrentTime() {
        NativeRoomRecording? recording = current;
        if (recording is null) return;
        branchStartSeconds = recording.MediaTimeSeconds;
        branchMusicStart = MusicPosition.Read();
        branchActive = true;
        waitingForStablePlayer = false;
    }

    private static void SuspendForPause() {
        if (pauseSuspended) return;
        NativeRoomRecording? recording = current;
        if (recording is not null && branchActive) {
            RecordingClip? completed = CurrentClip(recording.MediaTimeSeconds);
            if (completed is not null) ActivePrefix.Add(completed);
            branchActive = false;
        }
        pauseSuspended = true;
    }

    private static void ObserveMusicTimeline(NativeRoomRecording recording) {
        double now = recording.MediaTimeSeconds;
        MusicPosition observed = MusicPosition.Read();
        bool eventChanged = !string.Equals(observed.Event, branchMusicStart.Event, StringComparison.Ordinal);
        int expectedTimeline = branchMusicStart.TimelineMilliseconds
            + (int)Math.Round(Math.Max(0, now - branchStartSeconds) * 1_000.0);
        bool timelineJumped = observed.Event.Length > 0
            && Math.Abs((long)observed.TimelineMilliseconds - expectedTimeline)
                > MusicTimelineDiscontinuityMilliseconds;
        if (!eventChanged && !timelineJumped) return;

        RecordingClip? completed = CurrentClip(now);
        if (completed is not null) ActivePrefix.Add(completed);
        branchStartSeconds = now;
        branchMusicStart = observed;
    }

    private static void Complete(Level level) {
        FinalizeCurrent(level);
    }

    private static void FinalizeCurrent(Level level) {
        NativeRoomRecording? recording = current;
        if (completing
            || recording is null
            || !string.Equals(level.Session.Level, roomName, StringComparison.Ordinal)) {
            return;
        }
        completing = true;
        List<RecordingClip> clips = [.. ActivePrefix];
        if (branchActive) {
            RecordingClip? finalClip = CurrentClip(recording.MediaTimeSeconds);
            if (finalClip is not null) clips.Add(finalClip);
        }
        if (clips.Count == 0) {
            completing = false;
            DiscardCurrentRecording();
            return;
        }
        current = null;
        Task stop = recording.StopAsync();
        string output = Path.Combine(
            ResolveRecordingRoot(),
            Sanitize(areaSid),
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(roomName)}.mp4"
        );
        lastOutput = output;
        Interlocked.Increment(ref finalizingCount);
        _ = NativeRecordingFinalizer.FinishAsync(
            clips,
            [stop],
            [recording.Path, recording.AudioPath],
            output
        ).ContinueWith(_ => Interlocked.Decrement(ref finalizingCount), TaskScheduler.Default);
        ResetTimelineState();
    }

    private static RecordingClip? CurrentClip(double endSeconds) {
        NativeRoomRecording? recording = current;
        if (recording is null || !branchActive) return null;
        double duration = endSeconds - branchStartSeconds;
        if (duration < MinimumClipSeconds) return null;
        return new RecordingClip(
            recording.Path,
            Math.Max(0, branchStartSeconds),
            duration,
            branchMusicStart.Event,
            branchMusicStart.TimelineMilliseconds
        );
    }

    private static List<RecordingClip> CaptureCurrentClips(NativeRoomRecording recording) {
        List<RecordingClip> clips = [.. ActivePrefix];
        RecordingClip? currentClip = CurrentClip(recording.MediaTimeSeconds);
        if (currentClip is not null) clips.Add(currentClip);
        return clips;
    }

    private static bool ShouldRecord(Player player, QolSettings settings) {
        if (settings.RecordingPolicy == RecordingPolicy.EveryRoom) return true;
        return player.Leader.Followers.Any(follower => follower.Entity is Strawberry { Golden: true });
    }

    private static void DiscardCurrentRecording() {
        NativeRoomRecording? recording = current;
        current = null;
        if (recording is not null) {
            _ = recording.StopAsync().ContinueWith(_ => {
                try { File.Delete(recording.Path); } catch { }
                try { File.Delete(recording.AudioPath); } catch { }
            }, TaskScheduler.Default);
        }
        ActivePrefix.Clear();
        respawnAnchor = null;
        branchActive = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
    }

    private static void StopAndReset(bool deleteSource) {
        NativeRoomRecording? recording = current;
        current = null;
        if (recording is not null) {
            Task stop = recording.StopAsync();
            if (deleteSource) {
                _ = stop.ContinueWith(_ => {
                    try { File.Delete(recording.Path); } catch { }
                    try { File.Delete(recording.AudioPath); } catch { }
                }, TaskScheduler.Default);
            }
        }
        ResetTimelineState();
    }

    private static void ResetTimelineState() {
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = null;
        branchStartSeconds = 0;
        branchMusicStart = default;
        branchActive = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        roomKey = "";
        roomName = "";
        areaSid = "";
        completing = false;
    }

    private static int DeleteOldCompletedRecordings(string root, int retentionCount) {
        if (!Directory.Exists(root)) return 0;
        FileInfo[] completed = Directory
            .EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => File.Exists(file.FullName + ".timeline.json"))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        int deleted = 0;
        foreach (FileInfo file in completed.Skip(retentionCount)) {
            try {
                File.Delete(file.FullName);
                try { File.Delete(file.FullName + ".timeline.json"); } catch { }
                deleted++;
            } catch (Exception exception) {
                Logger.Log(
                    LogLevel.Warn,
                    "MicroblocksQolUtils/Recorder/Cleanup",
                    $"Cannot delete old recording {file.FullName}: {exception.Message}"
                );
            }
        }
        return deleted;
    }

    private static string ResolveRecordingRoot() {
        string configured = Environment.ExpandEnvironmentVariables(MicroblocksQolUtilsModule.Settings.RecordingDirectory.Trim());
        if (configured.Length > 0) return configured;
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (videos.Length == 0) videos = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(videos, "Celeste", "microblocks-qol-recordings");
    }

    private static string RoomKey(Level level) {
        return $"{level.Session.Area.SID}|{(int)level.Session.Area.Mode}|{level.Session.Level}";
    }

    private static string Sanitize(string value) {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '_' : character).ToArray());
    }
}
