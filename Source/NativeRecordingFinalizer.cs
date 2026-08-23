using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class NativeRecordingFinalizer {
    public static async Task FinishAsync(
        IReadOnlyList<RecordingClip> clips,
        IReadOnlyList<Task> pendingStops,
        IReadOnlyCollection<string> temporaryFiles,
        string output
    ) {
        bool completed = false;
        try {
            await Task.WhenAll(pendingStops).ConfigureAwait(false);
            if (clips.Count == 0 || clips.Any(clip => !File.Exists(clip.Source))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            QolSettings settings = MicroblocksQolUtilsModule.Settings;
            await NativeCaptureBridge.FinalizeRecordingAsync(
                clips,
                output,
                settings.RecordingEncoder,
                settings.RecordingBitrateKbps,
                settings.RecordingFrameRate,
                settings.BgmMode == BgmRecordingMode.SfxOnlyWithPostMix,
                settings.BgmEventMapFile
            ).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                output + ".timeline.json",
                JsonSerializer.Serialize(new { clips }, new JsonSerializerOptions { WriteIndented = true })
            ).ConfigureAwait(false);
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder", $"Saved successful room recording: {output}");
            completed = true;
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder");
        } finally {
            if (completed) {
                foreach (string file in temporaryFiles) {
                    try { File.Delete(file); } catch { }
                }
            } else {
                Logger.Log(
                    LogLevel.Warn,
                    "MicroblocksQolUtils/Recorder",
                    $"Finalization failed; preserved continuous recording files under {Path.GetDirectoryName(temporaryFiles.FirstOrDefault() ?? output)}"
                );
            }
        }
    }
}
