using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class NativeCaptureBridge {
    private const string LibraryName = "microblocks_qol_native";
    private const uint ExpectedAbiVersion = 4;
    private static bool initialized;
    private static bool available;
    private static string? loadError;

    public static bool Available => available;

    public static bool AuthorizationSupported => OperatingSystem.IsLinux();

    public static bool HasRecordingAuthorization() {
        if (!available) return false;
        return CaptureHasAuthorization() != 0;
    }

    public static Task<bool> AuthorizeRecordingAsync(bool force) {
        if (!available) return Task.FromResult(false);
        return Task.Run(() => CaptureAuthorize(force ? 1 : 0) == 0);
    }

    public static void InitializeFromMod(EverestModuleMetadata metadata) {
        ArgumentNullException.ThrowIfNull(metadata);
        Initialize(null);
    }

    public static void Initialize(string? nativeDirectory) {
        if (initialized) return;
        initialized = true;
        try {
            uint abi = CaptureAbiVersion();
            if (abi != ExpectedAbiVersion)
                throw new InvalidDataException($"native ABI {abi} != expected {ExpectedAbiVersion}");
            available = true;
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder",
                "Loaded native capture backend through Everest's platform library resolver");
        } catch (Exception exception) {
            available = false;
            loadError = $"cannot load native capture backend through Everest: {exception}";
            Logger.Log(LogLevel.Error, "MicroblocksQolUtils/Recorder", loadError);
        }
    }

    public static NativeCaptureSession Start(int fps, int queueCapacity = 3) {
        return StartCore(fps, queueCapacity, null, "auto", 12_000);
    }

    public static NativeCaptureSession StartRecording(
        int fps,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int queueCapacity = 3
    ) {
        return StartCore(fps, queueCapacity, Path.GetFullPath(outputPath), encoder, bitrateKbps);
    }

    private static NativeCaptureSession StartCore(
        int fps,
        int queueCapacity,
        string? outputPath,
        string encoder,
        int bitrateKbps
    ) {
        EnsureAvailable();
        ulong windowHandle = OperatingSystem.IsWindows() ? ResolveGameWindowHandle() : 0;
        if (OperatingSystem.IsWindows() && windowHandle == 0)
            throw new InvalidOperationException("Celeste HWND is not available yet");
        string windowTitle = OperatingSystem.IsWindows() ? "" : "Celeste";
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            window_title = windowTitle,
            fps,
            queue_capacity = queueCapacity,
            show_cursor = false,
            output_path = outputPath,
            encoder,
            bitrate_kbps = bitrateKbps,
            window_handle = windowHandle
        });
        int status = CaptureCreate(json, (nuint)json.Length, out ulong handle);
        ThrowIfFailed(status, "create");
        try {
            ThrowIfFailed(CaptureStart(handle), "start");
            return new NativeCaptureSession(handle);
        } catch {
            CaptureDestroy(handle);
            throw;
        }
    }

    private static ulong ResolveGameWindowHandle() {
        IntPtr window = Process.GetCurrentProcess().MainWindowHandle;
        if (window == IntPtr.Zero && Engine.Instance?.Window is { } gameWindow) {
            window = gameWindow.Handle;
        }
        return unchecked((ulong)window.ToInt64());
    }

    public static Task FinalizeRecordingAsync(
        IReadOnlyList<RecordingClip> clips,
        string outputPath,
        string encoder,
        int bitrateKbps,
        int fps,
        bool reconstructBgm,
        bool removeFreezeFrames,
        string bgmEventMapFile,
        Action<double>? progress = null
    ) {
        EnsureAvailable();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            clips = clips.Select(clip => new {
                source = Path.GetFullPath(clip.Source),
                start_seconds = clip.StartSeconds,
                duration_seconds = clip.DurationSeconds,
                music_event = clip.MusicEvent,
                music_timeline_milliseconds = clip.MusicTimelineMilliseconds,
                seamless_from_previous = clip.SeamlessFromPrevious
            }),
            output_path = Path.GetFullPath(outputPath),
            encoder,
            bitrate_kbps = bitrateKbps,
            fps,
            reconstruct_bgm = reconstructBgm,
            remove_freeze_frames = removeFreezeFrames,
            bgm_event_map_file = string.IsNullOrWhiteSpace(bgmEventMapFile)
                ? ""
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(bgmEventMapFile))
        });
        return Task.Run(() => {
            GCHandle progressHandle = default;
            FinalizeProgressCallback? callback = null;
            try {
                if (progress is not null) {
                    progressHandle = GCHandle.Alloc(progress);
                    callback = ReportFinalizeProgress;
                }
                ThrowIfFailed(RecordingFinalizeWithProgress(
                    json,
                    (nuint)json.Length,
                    callback,
                    progressHandle.IsAllocated ? GCHandle.ToIntPtr(progressHandle) : IntPtr.Zero
                ), "finalize");
                progress?.Invoke(1d);
                GC.KeepAlive(callback);
            } finally {
                if (progressHandle.IsAllocated) progressHandle.Free();
            }
        });
    }

    private static void ReportFinalizeProgress(float value, IntPtr context) {
        if (context == IntPtr.Zero) return;
        try {
            if (GCHandle.FromIntPtr(context).Target is Action<double> progress) {
                progress(Math.Clamp(value, 0f, 1f));
            }
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder",
                $"Cannot report finalization progress: {exception.Message}");
        }
    }

    private static void EnsureAvailable() {
        Initialize(null);
        if (!available) throw new DllNotFoundException(loadError ?? "native capture backend is unavailable");
    }

    private static void ThrowIfFailed(int status, string operation) {
        if (status == 0) return;
        throw new InvalidOperationException($"native capture {operation} failed ({status}): {LastError()}");
    }

    internal static string LastError() {
        nuint required = CaptureLastError(IntPtr.Zero, 0);
        if (required <= 1 || required > 64 * 1024) return "unknown native error";
        byte[] bytes = new byte[(int)required];
        unsafe {
            fixed (byte* pointer = bytes) CaptureLastError((IntPtr)pointer, (nuint)bytes.Length);
        }
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    internal static CaptureStatistics GetStats(ulong handle) {
        ThrowIfFailed(CaptureGetStats(handle, out NativeCaptureStats stats), "stats");
        return new CaptureStatistics(
            stats.Running != 0,
            stats.Width,
            stats.Height,
            stats.QueueDepth,
            stats.FramesCaptured,
            stats.FramesConsumed,
            stats.FramesDropped,
            stats.BytesCaptured,
            stats.LastFrameUnixNanos,
            stats.MediaTimeNanos,
            stats.AudioFramesCaptured,
            stats.AudioChunksDropped
        );
    }

    internal static void Stop(ulong handle) {
        int status = CaptureStop(handle);
        if (status != 0 && status != -4) ThrowIfFailed(status, "stop");
    }

    internal static void Destroy(ulong handle) {
        int status = CaptureDestroy(handle);
        if (status != 0 && status != -2) ThrowIfFailed(status, "destroy");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCaptureStats {
        public uint AbiVersion;
        public uint Running;
        public uint Width;
        public uint Height;
        public uint QueueDepth;
        public ulong FramesCaptured;
        public ulong FramesConsumed;
        public ulong FramesDropped;
        public ulong BytesCaptured;
        public ulong LastFrameUnixNanos;
        public ulong MediaTimeNanos;
        public ulong AudioFramesCaptured;
        public ulong AudioChunksDropped;
    }

    [DllImport(LibraryName, EntryPoint = "mqol_capture_abi_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint CaptureAbiVersion();

    [DllImport(LibraryName, EntryPoint = "mqol_capture_create", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureCreate(byte[] config, nuint configLength, out ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_start", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureStart(ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_stop", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureStop(ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_get_stats", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureGetStats(ulong handle, out NativeCaptureStats stats);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureDestroy(ulong handle);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_authorize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureAuthorize(int force);

    [DllImport(LibraryName, EntryPoint = "mqol_capture_has_authorization", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CaptureHasAuthorization();

    [DllImport(LibraryName, EntryPoint = "mqol_capture_push_audio", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int CapturePushAudio(
        ulong handle,
        float* samples,
        nuint sampleCount,
        uint sampleRate,
        ushort channels,
        ushort busId
    );

    [DllImport(LibraryName, EntryPoint = "mqol_capture_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint CaptureLastError(IntPtr buffer, nuint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FinalizeProgressCallback(float progress, IntPtr context);

    [DllImport(LibraryName, EntryPoint = "mqol_recording_finalize_with_progress",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int RecordingFinalizeWithProgress(
        byte[] plan,
        nuint planLength,
        FinalizeProgressCallback? progress,
        IntPtr context
    );
}

public sealed class NativeCaptureSession : IDisposable {
    private ulong handle;

    internal NativeCaptureSession(ulong handle) {
        this.handle = handle;
    }

    public CaptureStatistics Statistics => handle == 0
        ? default
        : NativeCaptureBridge.GetStats(handle);

    public void Stop() {
        if (handle != 0) NativeCaptureBridge.Stop(handle);
    }

    internal unsafe void PushAudio(float* samples, int sampleCount, int sampleRate, int channels, int busId) {
        ulong owned = handle;
        if (owned == 0 || samples is null || sampleCount <= 0) return;
        _ = NativeCaptureBridge.CapturePushAudio(
            owned,
            samples,
            (nuint)sampleCount,
            (uint)sampleRate,
            (ushort)channels,
            (ushort)busId
        );
    }

    public void Dispose() {
        ulong owned = Interlocked.Exchange(ref handle, 0);
        if (owned == 0) return;
        NativeCaptureBridge.Stop(owned);
        NativeCaptureBridge.Destroy(owned);
    }
}

public readonly record struct CaptureStatistics(
    bool Running,
    uint Width,
    uint Height,
    uint QueueDepth,
    ulong FramesCaptured,
    ulong FramesConsumed,
    ulong FramesDropped,
    ulong BytesCaptured,
    ulong LastFrameUnixNanos,
    ulong MediaTimeNanos,
    ulong AudioFramesCaptured,
    ulong AudioChunksDropped
) {
    public double MediaTimeSeconds => MediaTimeNanos / 1_000_000_000.0;
}
