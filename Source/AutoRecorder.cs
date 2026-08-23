using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class AutoRecorder {
    private const double MinimumClipSeconds = 0.02;
    private const int MusicTimelineDiscontinuityMilliseconds = 750;
    private const string FullRecordingsDirectory = "full";
    private const string DeathReplaysDirectory = "deaths";

    private static readonly List<RecordingClip> ActivePrefix = [];
    private static readonly List<PendingDeathReplay> PendingDeathReplays = [];
    private static NativeRoomRecording? current;
    private static RecordingTimelineSnapshot? respawnAnchor;
    private static Vector2? observedRespawnPoint;
    private static MusicPosition branchMusicStart;
    private static double branchStartSeconds;
    private static double deathReplayStartSeconds;
    private static string runKey = "";
    private static string areaSid = "";
    private static bool branchActive;
    private static bool waitingForStablePlayer;
    private static bool pauseSuspended;
    private static bool transitioningRoom;
    private static bool fullRecordingEnabled;
    private static bool completing;
    private static bool manualMode;
    private static int finalizingCount;
    private static int cleanupRunning;
    private static string lastOutput = "";
    private static string lastCleanupStatus = "—";

    public static bool ManualMode => manualMode;
    public static bool IsRecording => current is not null;
    public static bool IsFullRecordingEnabled => fullRecordingEnabled;
    public static bool IsFinalizing => Volatile.Read(ref finalizingCount) > 0;
    public static bool IsCleaning => Volatile.Read(ref cleanupRunning) != 0;
    public static double CurrentSeconds => current?.MediaTimeSeconds ?? 0;
    public static string CurrentPath => current?.Path ?? "";
    public static string LastOutput => lastOutput;
    public static string LastCleanupStatus => lastCleanupStatus;
    public static int PendingDeathReplayCount => PendingDeathReplays.Count;
    public static string RecordingRoot => ResolveRecordingRoot();
    public static string FullRecordingRoot => Path.Combine(ResolveRecordingRoot(), FullRecordingsDirectory);
    public static string DeathReplayRoot => Path.Combine(ResolveRecordingRoot(), DeathReplaysDirectory);

    public static void Load(string directory) {
        _ = directory;
        On.Celeste.Player.Die += PlayerDie;
        On.Celeste.Level.TransitionTo += LevelTransitionTo;
        On.Celeste.Level.RegisterAreaComplete += RegisterAreaComplete;
        Everest.Events.Level.OnEnd += LevelEnd;
        SpeedrunToolBridge.Load();
        CleanupRecordings();
    }

    public static void Unload() {
        manualMode = false;
        SpeedrunToolBridge.Unload();
        Everest.Events.Level.OnEnd -= LevelEnd;
        On.Celeste.Level.RegisterAreaComplete -= RegisterAreaComplete;
        On.Celeste.Level.TransitionTo -= LevelTransitionTo;
        On.Celeste.Player.Die -= PlayerDie;
        StopAndReset(deleteSource: true);
    }

    public static void Update(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if ((!settings.AutoRecorderEnabled && !settings.DeathReplayEnabled && !manualMode)
            || !OperatingSystem.IsWindows()) {
            if (current is not null || runKey.Length > 0) StopAndReset(deleteSource: true);
            return;
        }

        Player? player = level.Tracker.GetEntity<Player>();
        if (player is null) return;
        string key = RunKey(level);
        if (!string.Equals(key, runKey, StringComparison.Ordinal)) {
            if (runKey.Length > 0 && !completing) StopAndReset(deleteSource: true);
            BeginRun(level);
        }

        fullRecordingEnabled = manualMode
            || (settings.AutoRecorderEnabled && ShouldRecord(player, settings));
        if (!fullRecordingEnabled && !settings.DeathReplayEnabled) {
            if (current is not null) DiscardCurrentRecording();
            return;
        }

        if (level.Paused) {
            SuspendForPause();
            return;
        }

        if (current is null && !player.Dead && !level.Transitioning) {
            StartRunRecording(level);
        }
        NativeRoomRecording? recording = current;
        if (recording is null) return;

        if (pauseSuspended && !player.Dead && !level.Transitioning) {
            pauseSuspended = false;
            StartBranchAtCurrentTime(resetDeathReplayStart: false);
        } else if (waitingForStablePlayer && !player.Dead && !level.Transitioning) {
            StartBranchAtCurrentTime(resetDeathReplayStart: true);
        }

        if (branchActive && settings.BgmMode == BgmRecordingMode.SfxOnlyWithPostMix)
            ObserveMusicTimeline(recording);

        if (transitioningRoom) {
            if (level.Transitioning) return;
            transitioningRoom = false;
            respawnAnchor = new RecordingTimelineSnapshot(CaptureCurrentClips(recording));
            deathReplayStartSeconds = recording.MediaTimeSeconds;
            observedRespawnPoint = level.Session.RespawnPoint;
        }

        Vector2? respawn = level.Session.RespawnPoint;
        if (branchActive
            && RespawnPointChanged(observedRespawnPoint, respawn)) {
            respawnAnchor = new RecordingTimelineSnapshot(CaptureCurrentClips(recording));
            deathReplayStartSeconds = recording.MediaTimeSeconds;
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
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        int fullRetentionCount = Math.Max(0, settings.RecordingRetentionCount);
        int deathRetentionCount = Math.Max(0, settings.DeathReplayRetentionCount);
        if (fullRetentionCount == 0 && deathRetentionCount == 0) {
            lastCleanupStatus = "未启用保留上限";
            return;
        }
        if (Interlocked.Exchange(ref cleanupRunning, 1) != 0) return;
        lastCleanupStatus = "清理中";
        _ = Task.Run(() => {
            try {
                string root = ResolveRecordingRoot();
                int deleted = 0;
                if (fullRetentionCount > 0) {
                    deleted += DeleteOldCompletedRecordings(root, RecordingLibraryKind.Full, fullRetentionCount);
                }
                if (deathRetentionCount > 0) {
                    deleted += DeleteOldCompletedRecordings(root, RecordingLibraryKind.DeathReplay, deathRetentionCount);
                }
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
            || !string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) {
            return null;
        }
        return new RecordingTimelineSnapshot(
            CaptureCurrentClips(recording),
            respawnAnchor?.Clips.ToArray()
        );
    }

    public static void RestoreTimeline(Level level, RecordingTimelineSnapshot snapshot) {
        NativeRoomRecording? recording = current;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if ((!settings.AutoRecorderEnabled && !settings.DeathReplayEnabled) || recording is null) return;
        if (!string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) return;
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
        transitioningRoom = false;
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
        QueueDeathReplay(self, current);
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
        orig(self, next, direction);
        if (current is not null) transitioningRoom = true;
    }

    private static void RegisterAreaComplete(On.Celeste.Level.orig_RegisterAreaComplete orig, Level self) {
        Complete(self);
        orig(self);
    }

    private static void LevelEnd(
        Level level,
        Scene nextScene,
        ref bool shouldReloadPortraits,
        ref bool shouldDissociateEntities
    ) {
        _ = level;
        _ = nextScene;
        _ = shouldReloadPortraits;
        _ = shouldDissociateEntities;
        if (current is not null || runKey.Length > 0) StopAndReset(deleteSource: true);
    }

    private static void BeginRun(Level level) {
        completing = false;
        runKey = RunKey(level);
        areaSid = level.Session.Area.SID;
        observedRespawnPoint = level.Session.RespawnPoint;
        respawnAnchor = null;
        ActivePrefix.Clear();
        branchActive = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        transitioningRoom = false;
        fullRecordingEnabled = false;
    }

    private static void StartRunRecording(Level level) {
        string tempRoot = Path.Combine(ResolveRecordingRoot(), ".working", Sanitize(runKey));
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot, $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.mkv");
        current = NativeRoomRecording.Start(path);
        if (current is null) return;
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = level.Session.RespawnPoint;
        StartBranchAtCurrentTime(resetDeathReplayStart: true);
    }

    private static void StartBranchAtCurrentTime(bool resetDeathReplayStart) {
        NativeRoomRecording? recording = current;
        if (recording is null) return;
        branchStartSeconds = recording.MediaTimeSeconds;
        if (resetDeathReplayStart) deathReplayStartSeconds = branchStartSeconds;
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
            || !string.Equals(RunKey(level), runKey, StringComparison.Ordinal)) {
            return;
        }
        completing = true;
        List<RecordingClip> clips = [.. ActivePrefix];
        if (branchActive) {
            RecordingClip? finalClip = CurrentClip(recording.MediaTimeSeconds);
            if (finalClip is not null) clips.Add(finalClip);
        }
        current = null;
        Task stop = recording.StopAsync();
        List<RecordingFinalizationJob> jobs = TakeDeathReplayJobs();
        if (fullRecordingEnabled && clips.Count > 0) {
            string output = Path.Combine(
                FullRecordingRoot,
                Sanitize(areaSid),
                $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(areaSid)}.mp4"
            );
            lastOutput = output;
            jobs.Insert(0, new RecordingFinalizationJob(clips, output, "完整录像"));
        }
        FinishStoppedRecording(recording, stop, jobs);
        ResetTimelineState();
    }

    private static void QueueDeathReplay(Player player, NativeRoomRecording recording) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.DeathReplayEnabled) return;

        double endSeconds = recording.MediaTimeSeconds;
        List<RecordingClip> clips = CaptureClipsInRange(recording, deathReplayStartSeconds, endSeconds);
        if (clips.Count == 0) return;

        Level? level = player.Scene as Level;
        PendingDeathReplays.Add(new PendingDeathReplay(
            clips,
            DateTime.Now,
            level?.Session.Area.SID ?? areaSid,
            level?.Session.Level ?? "room"
        ));
        int retentionCount = Math.Max(0, settings.DeathReplayRetentionCount);
        if (retentionCount > 0 && PendingDeathReplays.Count > retentionCount) {
            PendingDeathReplays.RemoveRange(0, PendingDeathReplays.Count - retentionCount);
        }
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

    private static List<RecordingClip> CaptureClipsInRange(
        NativeRoomRecording recording,
        double startSeconds,
        double endSeconds
    ) {
        List<RecordingClip> result = [];
        foreach (RecordingClip clip in CaptureCurrentClips(recording)) {
            double clipEnd = clip.StartSeconds + clip.DurationSeconds;
            double retainedStart = Math.Max(startSeconds, clip.StartSeconds);
            double retainedEnd = Math.Min(endSeconds, clipEnd);
            double duration = retainedEnd - retainedStart;
            if (duration < MinimumClipSeconds) continue;
            int musicOffset = (int)Math.Round((retainedStart - clip.StartSeconds) * 1_000.0);
            result.Add(new RecordingClip(
                clip.Source,
                retainedStart,
                duration,
                clip.MusicEvent,
                clip.MusicTimelineMilliseconds + musicOffset
            ));
        }
        return result;
    }

    private static bool ShouldRecord(Player player, QolSettings settings) {
        if (settings.RecordingPolicy == RecordingPolicy.EveryRoom) return true;
        return player.Leader.Followers.Any(follower => follower.Entity is Strawberry { Golden: true });
    }

    private static void DiscardCurrentRecording() {
        NativeRoomRecording? recording = current;
        current = null;
        if (recording is not null) {
            FinishStoppedRecording(recording, recording.StopAsync(), TakeDeathReplayJobs());
        }
        ActivePrefix.Clear();
        respawnAnchor = null;
        deathReplayStartSeconds = 0;
        branchActive = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        transitioningRoom = false;
    }

    private static void StopAndReset(bool deleteSource) {
        NativeRoomRecording? recording = current;
        current = null;
        if (recording is not null) {
            Task stop = recording.StopAsync();
            if (deleteSource) {
                FinishStoppedRecording(recording, stop, TakeDeathReplayJobs());
            }
        }
        ResetTimelineState();
    }

    private static List<RecordingFinalizationJob> TakeDeathReplayJobs() {
        int retentionCount = Math.Max(0, MicroblocksQolUtilsModule.Settings.DeathReplayRetentionCount);
        IEnumerable<PendingDeathReplay> retained = retentionCount > 0
            ? PendingDeathReplays.TakeLast(retentionCount)
            : PendingDeathReplays;
        List<RecordingFinalizationJob> jobs = retained.Select(death => {
            string area = Sanitize(death.AreaSid);
            string room = Sanitize(death.Room);
            string unique = Guid.NewGuid().ToString("N")[..8];
            string fileName = $"{death.OccurredAt:yyyyMMdd-HHmmss-fff}-{room}-death-{unique}.mp4";
            string output = Path.Combine(DeathReplayRoot, area, fileName);
            return new RecordingFinalizationJob(death.Clips, output, "死亡回放");
        }).ToList();
        PendingDeathReplays.Clear();
        return jobs;
    }

    private static void FinishStoppedRecording(
        NativeRoomRecording recording,
        Task stop,
        IReadOnlyList<RecordingFinalizationJob> jobs
    ) {
        string[] temporaryFiles = [recording.Path, recording.AudioPath];
        if (jobs.Count == 0) {
            _ = stop.ContinueWith(_ => DeleteTemporaryFiles(temporaryFiles), TaskScheduler.Default);
            return;
        }

        Interlocked.Increment(ref finalizingCount);
        _ = FinishStoppedRecordingAsync(stop, temporaryFiles, jobs);
    }

    private static async Task FinishStoppedRecordingAsync(
        Task stop,
        IReadOnlyCollection<string> temporaryFiles,
        IReadOnlyList<RecordingFinalizationJob> jobs
    ) {
        bool completed = true;
        try {
            await stop.ConfigureAwait(false);
            foreach (RecordingFinalizationJob job in jobs) {
                if (!await NativeRecordingFinalizer.FinishAsync(
                    job.Clips,
                    job.Output,
                    job.Description
                ).ConfigureAwait(false)) {
                    completed = false;
                }
            }
            if (completed) DeleteTemporaryFiles(temporaryFiles);
            else {
                Logger.Log(
                    LogLevel.Warn,
                    "MicroblocksQolUtils/Recorder",
                    $"Finalization failed; preserved continuous recording files under {Path.GetDirectoryName(temporaryFiles.FirstOrDefault() ?? "")}"
                );
            }
            CleanupRecordings();
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder");
        } finally {
            Interlocked.Decrement(ref finalizingCount);
        }
    }

    private static void DeleteTemporaryFiles(IEnumerable<string> files) {
        foreach (string file in files) {
            try { File.Delete(file); } catch { }
        }
    }

    private static void ResetTimelineState() {
        ActivePrefix.Clear();
        respawnAnchor = null;
        observedRespawnPoint = null;
        branchStartSeconds = 0;
        deathReplayStartSeconds = 0;
        branchMusicStart = default;
        branchActive = false;
        waitingForStablePlayer = false;
        pauseSuspended = false;
        transitioningRoom = false;
        fullRecordingEnabled = false;
        runKey = "";
        areaSid = "";
        PendingDeathReplays.Clear();
        completing = false;
    }

    private static int DeleteOldCompletedRecordings(
        string root,
        RecordingLibraryKind kind,
        int retentionCount
    ) {
        if (!Directory.Exists(root)) return 0;
        FileInfo[] completed = Directory
            .EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => File.Exists(file.FullName + ".timeline.json")
                && RecordingLibrary.KindOf(root, file.FullName) == kind)
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

    private static string RunKey(Level level) {
        return $"{level.Session.Area.SID}|{(int)level.Session.Area.Mode}";
    }

    private static bool RespawnPointChanged(Vector2? previous, Vector2? currentPoint) {
        if (previous.HasValue != currentPoint.HasValue) return true;
        return previous.HasValue
            && Vector2.DistanceSquared(previous.Value, currentPoint!.Value) > 0.01f;
    }

    private static string Sanitize(string value) {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '_' : character).ToArray());
    }

    private sealed record PendingDeathReplay(
        IReadOnlyList<RecordingClip> Clips,
        DateTime OccurredAt,
        string AreaSid,
        string Room
    );

    private sealed record RecordingFinalizationJob(
        IReadOnlyList<RecordingClip> Clips,
        string Output,
        string Description
    );
}
