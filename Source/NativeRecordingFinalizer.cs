using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class NativeRecordingFinalizer {
    public static async Task<bool> FinishAsync(
        IReadOnlyList<RecordingClip> clips,
        string output,
        string description
    ) {
        try {
            if (clips.Count == 0 || clips.Any(clip => !File.Exists(clip.Source))) return false;
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
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder", $"Saved {description}: {output}");
            return true;
        } catch (Exception exception) {
            Logger.LogDetailed(exception, "MicroblocksQolUtils/Recorder");
            return false;
        }
    }
}
