using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class FrameProfiler {
    private static readonly Stopwatch FrameWatch = new();
    private static readonly Dictionary<string, long> UpdateTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> RenderTicks = new(StringComparer.Ordinal);
    private static long sampleStart;
    private static string? sampleOwner;
    private static Dictionary<string, long>? sampleTarget;
    private static double smoothedMilliseconds = 16.6667;
    private static DateTime lastWrittenAt = DateTime.MinValue;
    private static SpikeReport? latestSpike;

    public static double LastFrameMilliseconds { get; private set; } = 16.6667;
    public static double FramesPerSecond => smoothedMilliseconds <= 0.0001 ? 0 : 1000.0 / smoothedMilliseconds;

    public static void Load() {
        IL.Monocle.EntityList.Update += InstrumentEntityUpdate;
        IL.Monocle.EntityList.Render += InstrumentEntityRender;
        IL.Monocle.Engine.RenderCore += InstrumentFrameEnd;
    }

    public static void Unload() {
        IL.Monocle.Engine.RenderCore -= InstrumentFrameEnd;
        IL.Monocle.EntityList.Update -= InstrumentEntityUpdate;
        IL.Monocle.EntityList.Render -= InstrumentEntityRender;
    }

    public static void BeginFrame() {
        UpdateTicks.Clear();
        RenderTicks.Clear();
        FrameWatch.Restart();
    }

    public static void EndFrame() {
        FrameWatch.Stop();
        LastFrameMilliseconds = FrameWatch.Elapsed.TotalMilliseconds;
        smoothedMilliseconds += (LastFrameMilliseconds - smoothedMilliseconds) * 0.08;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.EnableFrameProfiler || LastFrameMilliseconds < settings.FrameSpikeThresholdMs) return;

        latestSpike = new SpikeReport(
            DateTime.Now,
            LastFrameMilliseconds,
            Top(UpdateTicks),
            Top(RenderTicks)
        );
        if ((DateTime.UtcNow - lastWrittenAt).TotalSeconds >= 1) {
            lastWrittenAt = DateTime.UtcNow;
            WriteSpike(latestSpike);
        }
    }

    public static void RenderHud(Vector2 position) {
        SpikeReport? spike = latestSpike;
        if (spike is null || (DateTime.Now - spike.At).TotalSeconds > 6) return;
        string update = spike.Update.Count == 0 ? "n/a" : $"{spike.Update[0].Owner} {spike.Update[0].Milliseconds:0.0}ms";
        string render = spike.Render.Count == 0 ? "n/a" : $"{spike.Render[0].Owner} {spike.Render[0].Milliseconds:0.0}ms";
        string text = $"SPIKE {spike.TotalMilliseconds:0.0}ms\nUpdate: {update}\nRender: {render}";
        SystemTtfFont.Draw(text, position, Vector2.Zero, 0.34f, Color.Orange, 1.25f);
    }

    private static void InstrumentEntityUpdate(ILContext il) => Instrument(il, "Update", BeginUpdateSample);
    private static void InstrumentEntityRender(ILContext il) => Instrument(il, "Render", BeginRenderSample);

    private static void InstrumentFrameEnd(ILContext il) {
        ILCursor cursor = new(il);
        if (!cursor.TryGotoNext(MoveType.Before, instruction => instruction.MatchRet())) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Profiler", "Could not instrument Engine.RenderCore");
            return;
        }
        cursor.EmitDelegate(EndFrame);
    }

    private static void Instrument(ILContext il, string method, Action<Entity> begin) {
        ILCursor cursor = new(il);
        if (!cursor.TryGotoNext(MoveType.Before, instruction => instruction.MatchCallvirt<Entity>(method))) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Profiler", $"Could not instrument Entity.{method}");
            return;
        }
        cursor.Emit(OpCodes.Dup);
        cursor.EmitDelegate(begin);
        cursor.Index++;
        cursor.EmitDelegate(EndEntitySample);
    }

    private static void BeginUpdateSample(Entity entity) => BeginEntitySample(entity, UpdateTicks);
    private static void BeginRenderSample(Entity entity) => BeginEntitySample(entity, RenderTicks);

    private static void BeginEntitySample(Entity entity, Dictionary<string, long> target) {
        if (!MicroblocksQolUtilsModule.Settings.EnableFrameProfiler) return;
        sampleOwner = entity.GetType().Assembly.GetName().Name ?? entity.GetType().Namespace ?? "unknown";
        sampleTarget = target;
        sampleStart = Stopwatch.GetTimestamp();
    }

    private static void EndEntitySample() {
        if (sampleTarget is null || sampleOwner is null) return;
        long elapsed = Stopwatch.GetTimestamp() - sampleStart;
        sampleTarget[sampleOwner] = sampleTarget.GetValueOrDefault(sampleOwner) + elapsed;
        sampleTarget = null;
        sampleOwner = null;
    }

    private static List<ProfileEntry> Top(Dictionary<string, long> values) => values
        .Select(pair => new ProfileEntry(pair.Key, pair.Value * 1000.0 / Stopwatch.Frequency))
        .OrderByDescending(entry => entry.Milliseconds)
        .Take(8)
        .ToList();

    private static void WriteSpike(SpikeReport spike) {
        try {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MicroblocksQolUtils",
                "profiles"
            );
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, $"spikes-{DateTime.Now:yyyy-MM-dd}.csv");
            bool header = !File.Exists(path);
            StringBuilder line = new();
            if (header) line.AppendLine("timestamp,total_ms,phase,owner,milliseconds");
            foreach ((string phase, IReadOnlyList<ProfileEntry> entries) in new[] {
                ("update", (IReadOnlyList<ProfileEntry>)spike.Update),
                ("render", spike.Render)
            }) {
                foreach (ProfileEntry entry in entries) {
                    line.Append(spike.At.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                        .Append(spike.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                        .Append(phase).Append(',')
                        .Append('"').Append(entry.Owner.Replace("\"", "\"\"")).Append('"').Append(',')
                        .Append(entry.Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine();
                }
            }
            File.AppendAllText(path, line.ToString(), Encoding.UTF8);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Profiler", exception.Message);
        }
    }

    private sealed record SpikeReport(DateTime At, double TotalMilliseconds, List<ProfileEntry> Update, List<ProfileEntry> Render);
    private sealed record ProfileEntry(string Owner, double Milliseconds);
}
