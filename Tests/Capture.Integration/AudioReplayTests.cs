using System.Reflection;
using Celeste.Mod.MicroblocksQolUtils;

internal static class AudioReplayTests {
    private static void Check(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
    private static void Ok(FMOD.RESULT result) => Check(result == FMOD.RESULT.OK, result.ToString());

    internal static void Run(FMOD.Studio.System studio, string output) {
        string journalPath = Path.Combine(output, "music-start.music.jsonl");
        using (var journal = new MusicJournal(journalPath)) {
            using var subscription = CaptureSource.Subscribe(music: journal.Accept);
            journal.Start(SdlFrameSource.ClockNanos());
            Ok(studio.getEvent("event:/music/lvl1/main", out var description));
            Ok(description.createInstance(out var song));
            // Match Audio.SetMusic: start is issued before the new instance is assigned.
            Ok(song.start());
            typeof(Celeste.Audio).GetField("currentMusicEvent", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, song);
            CaptureSource.Update();
            for (int i = 0; i < 40; i++) {
                Ok(studio.update()); Thread.Sleep(10); CaptureSource.Update();
            }
            subscription.Complete(); subscription.Completion.GetAwaiter().GetResult();
            Check(CaptureSource.MusicError is null, "Music capture failed: " + CaptureSource.MusicError);
            journal.Finish(true);
            Ok(song.stop(FMOD.Studio.STOP_MODE.IMMEDIATE)); Ok(song.release());
        }
        RecordingClip[] clips = [new("run.mkv", 0, 8, "", 0)];
        var commands = MusicEventReplayRenderer.Plan(journalPath, clips, true);
        Check(commands.Any(c => c.Operation == "start"), "A newly started song produced no replay start command");
        string sidecar = Path.Combine(output, "music-start.bgmchunks");
        Check(MusicEventReplayRenderer.RenderToSidecar(journalPath, sidecar, clips, true), "Music render failed");
        var samples = ReadSamples(sidecar);
        for (int second = 1; second < 7; second++) {
            double rms = Rms(samples, second, 1);
            Console.WriteLine($"BGM second {second}: RMS {rms:F6}");
            Check(rms > 0.0001, $"BGM second {second} is silent");
        }
        // Recorded STOPPED -> PLAYING races must not leave an entire take silent.
        string delayedPath = Path.Combine(output, "delayed.music.jsonl");
        using (var journal = new MusicJournal(delayedPath)) {
            journal.Start(0);
            journal.Accept(new(0, 1, "switch", "main", "event:/music/lvl1/main", 1, 0, false, "STOPPED", new Dictionary<string, float>()));
            journal.Accept(new(10_000_000, 2, "playback", "main", "event:/music/lvl1/main", 1, 0, false, "PLAYING", new Dictionary<string, float>()));
            journal.Finish(true);
        }
        Check(MusicEventReplayRenderer.Plan(delayedPath, clips, true).Count(c => c.Operation == "start") == 1,
            "Delayed playback was never started");
        Ok(studio.getEvent("event:/music/lvl1/main", out var vanilla));
        Ok(vanilla.getID(out Guid vanillaId));
        Check(AudioEventReplayRenderer.RenderCommandsToSidecar(
            [new(0, 1, "guid://" + vanillaId, "start", Bus: "music")], sidecar, 3), "GUID music render failed");
        Check(Rms(ReadSamples(sidecar), 1, 1) > 0.0001, "GUID music is silent");
        Check(!AudioEventReplayRenderer.RenderCommandsToSidecar(
            [new(0, 1, "event:/missing/replay-regression", "start", Bus: "music")], sidecar, 1),
            "Missing music event was reported as success");
        TestModBank(studio, output);
    }

    private sealed class BankAsset(byte[] data) : Celeste.Mod.ModAsset(null!) {
        protected override void Open(out Stream stream, out bool async) {
            stream = new MemoryStream(data, false); async = false;
        }
    }

    private static void TestModBank(FMOD.Studio.System studio, string output) {
        string? zipPath = Environment.GetEnvironmentVariable("MQOL_TEST_MOD_BANK_ZIP");
        if (zipPath is null) { Console.WriteLine("SKIP optional custom-bank replay (MQOL_TEST_MOD_BANK_ZIP)"); return; }
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.First(e => e.FullName.EndsWith(".bank", StringComparison.OrdinalIgnoreCase));
        using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
        byte[] data = bytes.ToArray();
        Ok(studio.loadBankMemory(data, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank));
        var asset = new BankAsset(data) { PathVirtual = entry.FullName };
        Celeste.Audio.Banks.ModCache.Add(asset, bank);
        try {
            Ok(bank.getEventList(out var events));
            Check(events.Length > 0, "Test mod bank has no events");
            Ok(events[0].getID(out Guid id));
            // Simulate Everest's .guids alias without shipping someone else's bank.
            var aliases = (Dictionary<Guid, string>)typeof(Celeste.Audio)
                .GetField("cachedPaths", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
            string alias = "event:/music/mqol-custom-bank-test";
            bool hadAlias = aliases.TryGetValue(id, out string? oldAlias);
            aliases[id] = alias;
            try {
                string sidecar = Path.Combine(output, "custom.bgmchunks");
                Check(AudioEventReplayRenderer.RenderCommandsToSidecar(
                    [new(0, 1, alias, "start", Bus: "music")], sidecar, 8), "Custom bank alias render failed");
                double rms = Rms(ReadSamples(sidecar), 0, 8);
                Console.WriteLine($"Custom bank BGM RMS: {rms:F6}");
                Check(rms > 0.0001, "Custom bank music is silent");
            } finally {
                if (hadAlias) aliases[id] = oldAlias!; else aliases.Remove(id);
            }
        } finally { Celeste.Audio.Banks.ModCache.Remove(asset); Ok(bank.unload()); }
    }

    internal static float[] ReadSamples(string path) {
        using var reader = new BinaryReader(File.OpenRead(path));
        Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)) == "MQOLAUD1", "bad sidecar header");
        List<float> samples = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length) {
            _ = reader.ReadUInt64();
            Check(reader.ReadInt32() == 48000 && reader.ReadUInt16() == 2, "unexpected audio format");
            _ = reader.ReadUInt16(); _ = reader.ReadUInt32();
            uint count = reader.ReadUInt32();
            for (int i = 0; i < count; i++) samples.Add(reader.ReadSingle());
        }
        return samples.ToArray();
    }
    internal static double Rms(float[] samples, double start, double duration) {
        var values = samples.Skip((int)(start * 96000)).Take((int)(duration * 96000)).ToArray();
        return values.Length == 0 ? 0 : Math.Sqrt(values.Average(v => (double)v * v));
    }
}
