using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

internal enum ManagedSamplingStage {
    Idle,
    WarmingUp,
    Sampling,
    Analyzing,
    Complete,
    Failed
}

internal sealed record ManagedProfileEntry(
    string Owner,
    string Method,
    string? HookTarget,
    bool IsMod,
    double Milliseconds,
    double Percent
);

internal sealed record ManagedProfileReport(
    DateTime CapturedAt,
    double DurationSeconds,
    int StackSamples,
    double UpdateCpuMilliseconds,
    double RenderCpuMilliseconds,
    double OtherCpuMilliseconds,
    int FrameCount,
    double AverageFrameMilliseconds,
    double MaximumFrameMilliseconds,
    double MaximumUpdateMilliseconds,
    double MaximumRenderMilliseconds,
    IReadOnlyList<ManagedProfileEntry> Update,
    IReadOnlyList<ManagedProfileEntry> Render,
    string TracePath,
    string SummaryPath
);

internal static class ManagedCpuSampler {
    private const double DefaultDurationSeconds = 10d;
    private const double WarmupSeconds = 0.75d;
    private static readonly object StateLock = new();
    private static readonly object FrameLock = new();
    private static readonly Stopwatch StageWatch = new();

    private static CancellationTokenSource? cancellation;
    private static EventPipeSession? activeSession;
    private static Task? worker;
    private static ManagedSamplingStage stage;
    private static ManagedProfileReport? latestReport;
    private static string failure = "";
    private static double requestedDuration = DefaultDurationSeconds;
    private static FrameStatistics frames;

    public static ManagedSamplingStage Stage {
        get { lock (StateLock) return stage; }
    }

    public static ManagedProfileReport? LatestReport {
        get { lock (StateLock) return latestReport; }
    }

    public static string Failure {
        get { lock (StateLock) return failure; }
    }

    public static bool IsBusy => Stage is ManagedSamplingStage.WarmingUp
        or ManagedSamplingStage.Sampling
        or ManagedSamplingStage.Analyzing;

    public static float Progress {
        get {
            lock (StateLock) {
                return stage switch {
                    ManagedSamplingStage.WarmingUp => (float)Math.Clamp(StageWatch.Elapsed.TotalSeconds / WarmupSeconds, 0d, 1d),
                    ManagedSamplingStage.Sampling => (float)Math.Clamp(StageWatch.Elapsed.TotalSeconds / requestedDuration, 0d, 1d),
                    ManagedSamplingStage.Analyzing or ManagedSamplingStage.Complete => 1f,
                    _ => 0f
                };
            }
        }
    }

    public static double RemainingSeconds {
        get {
            lock (StateLock) {
                return stage switch {
                    ManagedSamplingStage.WarmingUp => Math.Max(0d, WarmupSeconds - StageWatch.Elapsed.TotalSeconds),
                    ManagedSamplingStage.Sampling => Math.Max(0d, requestedDuration - StageWatch.Elapsed.TotalSeconds),
                    _ => 0d
                };
            }
        }
    }

    public static bool Start(double durationSeconds = DefaultDurationSeconds) {
        lock (StateLock) {
            if (stage is ManagedSamplingStage.WarmingUp or ManagedSamplingStage.Sampling or ManagedSamplingStage.Analyzing)
                return false;
            requestedDuration = Math.Clamp(durationSeconds, 2d, 60d);
            failure = "";
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            stage = ManagedSamplingStage.WarmingUp;
            StageWatch.Restart();
            lock (FrameLock) frames = default;
            ModAssemblySnapshot mods = ModAssemblySnapshot.Capture();
            worker = Task.Run(() => RunAsync(requestedDuration, mods, cancellation.Token));
            return true;
        }
    }

    public static void RecordFrame(double updateMilliseconds, double renderMilliseconds) {
        if (Stage != ManagedSamplingStage.Sampling) return;
        lock (FrameLock) {
            double total = updateMilliseconds + renderMilliseconds;
            frames.Count++;
            frames.TotalMilliseconds += total;
            frames.MaximumMilliseconds = Math.Max(frames.MaximumMilliseconds, total);
            frames.MaximumUpdateMilliseconds = Math.Max(frames.MaximumUpdateMilliseconds, updateMilliseconds);
            frames.MaximumRenderMilliseconds = Math.Max(frames.MaximumRenderMilliseconds, renderMilliseconds);
        }
    }

    public static void Unload() {
        CancellationTokenSource? source;
        EventPipeSession? session;
        lock (StateLock) {
            source = cancellation;
            session = activeSession;
            cancellation = null;
            activeSession = null;
            stage = ManagedSamplingStage.Idle;
            StageWatch.Reset();
        }
        source?.Cancel();
        try { session?.Stop(); } catch { }
        source?.Dispose();
    }

    private static async Task RunAsync(
        double durationSeconds,
        ModAssemblySnapshot mods,
        CancellationToken token
    ) {
        string? tracePath = null;
        string? etlxPath = null;
        try {
            await Task.Delay(TimeSpan.FromSeconds(WarmupSeconds), token).ConfigureAwait(false);
            SetStage(ManagedSamplingStage.Sampling);

            string root = ProfileDirectory();
            string stem = $"managed-{DateTime.Now:yyyyMMdd-HHmmss}";
            tracePath = Path.Combine(root, stem + ".nettrace");
            etlxPath = Path.Combine(root, stem + ".etlx");
            HookSnapshot hooks = HookSnapshot.Capture();

            List<EventPipeProvider> providers = [
                new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
            ];
            using EventPipeSession session = new DiagnosticsClient(Environment.ProcessId)
                .StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 128);
            lock (StateLock) activeSession = session;
            await using (FileStream output = new(tracePath, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                Task copy = session.EventStream.CopyToAsync(output, token);
                await Task.Delay(TimeSpan.FromSeconds(durationSeconds), token).ConfigureAwait(false);
                session.Stop();
                await copy.ConfigureAwait(false);
            }
            lock (StateLock) activeSession = null;

            SetStage(ManagedSamplingStage.Analyzing);
            ManagedProfileReport report = Analyze(tracePath, etlxPath, durationSeconds, hooks, mods);
            lock (StateLock) {
                latestReport = report;
                stage = ManagedSamplingStage.Complete;
                StageWatch.Stop();
            }
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Profiler",
                $"Managed CPU sampling complete: {report.StackSamples} samples, {report.SummaryPath}");
        } catch (OperationCanceledException) {
            lock (StateLock) {
                if (stage != ManagedSamplingStage.Idle) stage = ManagedSamplingStage.Idle;
                activeSession = null;
                StageWatch.Reset();
            }
        } catch (Exception exception) {
            lock (StateLock) {
                failure = exception.GetBaseException().Message;
                stage = ManagedSamplingStage.Failed;
                activeSession = null;
                StageWatch.Stop();
            }
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Profiler");
        }
    }

    private static void SetStage(ManagedSamplingStage value) {
        lock (StateLock) {
            stage = value;
            StageWatch.Restart();
        }
    }

    private static ManagedProfileReport Analyze(
        string tracePath,
        string etlxPath,
        double durationSeconds,
        HookSnapshot hooks,
        ModAssemblySnapshot mods
    ) {
        TraceLog.CreateFromEventPipeDataFile(tracePath, etlxPath);
        using TraceLog trace = new(etlxPath);
        using SymbolReader symbols = new(TextWriter.Null, new SymbolPath().LocalOnly().ToString());
        MutableTraceEventStackSource stacks = new(trace);
        new SampleProfilerThreadTimeComputer(trace, symbols).GenerateThreadTimeStacks(stacks, trace.Events);
        stacks.DoneAddingSamples();

        Dictionary<ProfileKey, double> update = [];
        Dictionary<ProfileKey, double> render = [];
        double updateTotal = 0d;
        double renderTotal = 0d;
        double otherTotal = 0d;
        int sampleCount = 0;

        stacks.ForEach(sample => {
            List<string> framesForSample = ReadFrames(stacks, sample.StackIndex);
            ProfilePhase phase = Classify(framesForSample);
            double metric = Math.Max(0d, sample.Metric);
            if (phase == ProfilePhase.Other) {
                otherTotal += metric;
                return;
            }

            sampleCount++;
            // A sample is charged to exactly one leaf-most actionable frame. This is exclusive/self
            // time: if hook A calls orig and execution is currently in hook B or the original method,
            // that sample belongs to B/orig rather than being counted again against A.
            string? exclusiveFrame = FindExclusiveFrame(framesForSample);
            if (exclusiveFrame is null)
                exclusiveFrame = phase == ProfilePhase.Update ? "unknown!Update" : "unknown!Render";
            string owner = FrameOwner(exclusiveFrame);
            string? hookTarget = hooks.TargetFor(exclusiveFrame, framesForSample);
            ProfileKey key = new(owner, CleanMethod(exclusiveFrame), hookTarget);
            Dictionary<ProfileKey, double> target = phase == ProfilePhase.Update ? update : render;
            target[key] = target.GetValueOrDefault(key) + metric;
            if (phase == ProfilePhase.Update) updateTotal += metric;
            else renderTotal += metric;
        });

        FrameStatistics frameStats;
        lock (FrameLock) frameStats = frames;
        IReadOnlyList<ManagedProfileEntry> updateEntries = MakeEntries(update, updateTotal, mods);
        IReadOnlyList<ManagedProfileEntry> renderEntries = MakeEntries(render, renderTotal, mods);
        string summaryPath = Path.ChangeExtension(tracePath, ".csv");
        ManagedProfileReport report = new(
            DateTime.Now,
            durationSeconds,
            sampleCount,
            updateTotal,
            renderTotal,
            otherTotal,
            frameStats.Count,
            frameStats.Count == 0 ? 0d : frameStats.TotalMilliseconds / frameStats.Count,
            frameStats.MaximumMilliseconds,
            frameStats.MaximumUpdateMilliseconds,
            frameStats.MaximumRenderMilliseconds,
            updateEntries,
            renderEntries,
            tracePath,
            summaryPath
        );
        WriteSummary(report);
        return report;
    }

    private static List<string> ReadFrames(
        MutableTraceEventStackSource stacks,
        StackSourceCallStackIndex stack
    ) {
        List<string> result = new(24);
        int depth = 0;
        while ((int)stack >= 0 && depth++ < 192) {
            StackSourceFrameIndex frame = stacks.GetFrameIndex(stack);
            result.Add(stacks.GetFrameName(frame, false));
            stack = stacks.GetCallerIndex(stack);
        }
        return result;
    }

    private static ProfilePhase Classify(IReadOnlyList<string> framesForSample) {
        foreach (string frame in framesForSample) {
            if (frame.Contains("MicroblocksQolUtilsModule.EngineUpdate", StringComparison.Ordinal)
                || frame.Contains("Monocle.Engine.Update(", StringComparison.Ordinal)) return ProfilePhase.Update;
            if (frame.Contains("MicroblocksQolUtilsModule.EngineDraw", StringComparison.Ordinal)
                || frame.Contains("Monocle.Engine.Draw(", StringComparison.Ordinal)
                || frame.Contains("Monocle.Engine.RenderCore(", StringComparison.Ordinal)) return ProfilePhase.Render;
        }
        return ProfilePhase.Other;
    }

    private static string? FindExclusiveFrame(IReadOnlyList<string> framesForSample) {
        string? fallback = null;
        foreach (string frame in framesForSample) {
            if (IsPseudoFrame(frame)) continue;
            string owner = FrameOwner(frame);
            if (owner.Length == 0) continue;
            fallback ??= frame;
            if (IsInfrastructure(owner, frame)) continue;
            return frame;
        }
        return fallback;
    }

    private static bool IsPseudoFrame(string frame) => frame is "CPU_TIME" or "UNMANAGED_CODE_TIME"
        || frame.StartsWith("Thread (", StringComparison.Ordinal)
        || frame.StartsWith("Process ", StringComparison.Ordinal);

    private static bool IsInfrastructure(string owner, string frame) {
        if (owner.StartsWith("System", StringComparison.Ordinal)
            || owner.StartsWith("Microsoft.", StringComparison.Ordinal)
            || owner.StartsWith("MonoMod.", StringComparison.Ordinal)
            || owner is "mscorlib" or "netstandard") return true;
        return owner == "MicroblocksQolUtils"
            && (frame.Contains("ManagedCpuSampler", StringComparison.Ordinal)
                || frame.Contains("FrameProfiler.EndRender", StringComparison.Ordinal));
    }

    private static string FrameOwner(string frame) {
        int separator = frame.IndexOf('!');
        return separator <= 0 ? "" : frame[..separator];
    }

    private static string CleanMethod(string frame) {
        int separator = frame.IndexOf('!');
        string method = separator >= 0 && separator + 1 < frame.Length ? frame[(separator + 1)..] : frame;
        int arguments = method.IndexOf('(');
        if (arguments > 0) method = method[..arguments];
        return method.Replace("class ", "", StringComparison.Ordinal)
            .Replace("value class ", "", StringComparison.Ordinal);
    }

    private static IReadOnlyList<ManagedProfileEntry> MakeEntries(
        Dictionary<ProfileKey, double> values,
        double total,
        ModAssemblySnapshot mods
    ) => values
        .Select(pair => new ManagedProfileEntry(
            pair.Key.Owner,
            pair.Key.Method,
            pair.Key.HookTarget,
            mods.Contains(pair.Key.Owner)
                || pair.Key.HookTarget is not null
                || pair.Key.Method.StartsWith("Celeste.Mod.", StringComparison.Ordinal),
            pair.Value,
            total <= 0d ? 0d : pair.Value / total * 100d
        ))
        .OrderByDescending(entry => entry.Milliseconds)
        .Take(128)
        .ToList();

    private static void WriteSummary(ManagedProfileReport report) {
        StringBuilder csv = new("phase,owner,method,hook_target,is_mod,exclusive_cpu_ms,percent_of_phase\n");
        foreach ((string phase, IReadOnlyList<ManagedProfileEntry> entries) in new[] {
            ("update", report.Update),
            ("render", report.Render)
        }) {
            foreach (ManagedProfileEntry entry in entries) {
                csv.Append(phase).Append(',')
                    .Append(Csv(entry.Owner)).Append(',')
                    .Append(Csv(entry.Method)).Append(',')
                    .Append(Csv(entry.HookTarget ?? "")).Append(',')
                    .Append(entry.IsMod ? "true" : "false").Append(',')
                    .Append(entry.Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(entry.Percent.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
            }
        }
        File.WriteAllText(report.SummaryPath, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string ProfileDirectory() {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MicroblocksQolUtils",
            "profiles"
        );
        Directory.CreateDirectory(root);
        return root;
    }

    private readonly record struct ProfileKey(string Owner, string Method, string? HookTarget);

    private enum ProfilePhase {
        Other,
        Update,
        Render
    }

    private struct FrameStatistics {
        public int Count;
        public double TotalMilliseconds;
        public double MaximumMilliseconds;
        public double MaximumUpdateMilliseconds;
        public double MaximumRenderMilliseconds;
    }

    private sealed class HookSnapshot {
        private readonly List<HookDescriptor> descriptors;

        private HookSnapshot(List<HookDescriptor> descriptors) {
            this.descriptors = descriptors;
        }

        public static HookSnapshot Capture() {
            List<HookDescriptor> result = [];
            try {
                FieldInfo? statesField = typeof(DetourManager).GetField(
                    "detourStates",
                    BindingFlags.NonPublic | BindingFlags.Static
                );
                if (statesField?.GetValue(null) is not IEnumerable states) return new HookSnapshot(result);
                foreach (object? item in states) {
                    MethodBase? source = item?.GetType().GetProperty("Key")?.GetValue(item) as MethodBase;
                    if (source is null) continue;
                    MethodDetourInfo info = DetourManager.GetDetourInfo(source);
                    foreach (DetourInfo detour in info.Detours) {
                        MethodBase entry = detour.Entry;
                        string? assembly = entry.DeclaringType?.Assembly.GetName().Name;
                        string? type = entry.DeclaringType?.FullName;
                        if (assembly is null || type is null) continue;
                        string prefix = $"{assembly}!{type}.{entry.Name}";
                        string target = $"{source.DeclaringType?.FullName ?? "?"}.{source.Name}";
                        result.Add(new HookDescriptor(assembly, prefix, target));
                    }
                }
            } catch (Exception exception) {
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Profiler",
                    $"Could not snapshot detour owners: {exception.Message}");
            }
            return new HookSnapshot(result);
        }

        public string? TargetFor(string frame, IReadOnlyList<string> stack) {
            foreach (HookDescriptor descriptor in descriptors) {
                if (frame.StartsWith(descriptor.FramePrefix, StringComparison.Ordinal)) return descriptor.Target;
            }
            string owner = FrameOwner(frame);
            foreach (string candidate in stack) {
                foreach (HookDescriptor descriptor in descriptors) {
                    if (descriptor.Owner == owner
                        && candidate.StartsWith(descriptor.FramePrefix, StringComparison.Ordinal))
                        return descriptor.Target;
                }
            }
            return null;
        }
    }

    private sealed record HookDescriptor(string Owner, string FramePrefix, string Target);

    private sealed class ModAssemblySnapshot {
        private readonly HashSet<string> owners;

        private ModAssemblySnapshot(HashSet<string> owners) {
            this.owners = owners;
        }

        public static ModAssemblySnapshot Capture() {
            HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
            foreach (EverestModule module in Everest.Modules) {
                string? assembly = module.GetType().Assembly.GetName().Name;
                if (!string.IsNullOrWhiteSpace(assembly)) result.Add(assembly);
            }
            return new ModAssemblySnapshot(result);
        }

        public bool Contains(string owner) => owners.Contains(owner);
    }
}
