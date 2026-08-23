using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class NativeCaptureBridge {
    private const string LibraryName = "microblocks_qol_native";
    private const uint ExpectedAbiVersion = 3;
    private static bool resolverInstalled;
    private static IntPtr nativeHandle;
    private static string? configuredNativeDirectory;
    private static string? loadError;

    public static bool Available => nativeHandle != IntPtr.Zero;

    public static void Initialize(string? nativeDirectory) {
        if (resolverInstalled) return;
        configuredNativeDirectory = string.IsNullOrWhiteSpace(nativeDirectory)
            ? null
            : Path.GetFullPath(nativeDirectory);
        NativeLibrary.SetDllImportResolver(typeof(NativeCaptureBridge).Assembly, ResolveLibrary);
        resolverInstalled = true;
        string? candidate = NativeLibraryPath(typeof(NativeCaptureBridge).Assembly);
        if (candidate is null) {
            loadError = "microblocks_qol_native.dll was not found beside the managed mod DLL";
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", loadError);
            return;
        }
        try {
            nativeHandle = NativeLibrary.Load(candidate);
            uint abi = CaptureAbiVersion();
            if (abi != ExpectedAbiVersion)
                throw new InvalidDataException($"native ABI {abi} != expected {ExpectedAbiVersion}");
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils/Recorder", $"Loaded scap native capture backend: {candidate}");
        } catch (Exception exception) {
            nativeHandle = IntPtr.Zero;
            loadError = $"cannot load native capture backend at {candidate}: {exception}";
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
        ulong windowHandle = ResolveGameWindowHandle();
        if (windowHandle == 0)
            throw new InvalidOperationException("Celeste HWND is not available yet");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            window_title = "",
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
        string bgmEventMapFile
    ) {
        EnsureAvailable();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
            clips = clips.Select(clip => new {
                source = Path.GetFullPath(clip.Source),
                start_seconds = clip.StartSeconds,
                duration_seconds = clip.DurationSeconds,
                music_event = clip.MusicEvent,
                music_timeline_milliseconds = clip.MusicTimelineMilliseconds
            }),
            output_path = Path.GetFullPath(outputPath),
            encoder,
            bitrate_kbps = bitrateKbps,
            fps,
            reconstruct_bgm = reconstructBgm,
            bgm_event_map_file = string.IsNullOrWhiteSpace(bgmEventMapFile)
                ? ""
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(bgmEventMapFile))
        });
        return Task.Run(() => ThrowIfFailed(RecordingFinalize(json, (nuint)json.Length), "finalize"));
    }

    private static void EnsureAvailable() {
        Initialize(null);
        if (nativeHandle == IntPtr.Zero) throw new DllNotFoundException(loadError ?? "native capture backend is unavailable");
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

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return IntPtr.Zero;
        if (nativeHandle != IntPtr.Zero) return nativeHandle;
        string? candidate = NativeLibraryPath(assembly);
        if (candidate is null) return IntPtr.Zero;
        try {
            return nativeHandle = NativeLibrary.Load(candidate);
        } catch (Exception exception) {
            loadError = exception.ToString();
            return IntPtr.Zero;
        }
    }

    private static string? NativeLibraryPath(Assembly assembly) {
        string? directory = configuredNativeDirectory ?? Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrWhiteSpace(directory) || !OperatingSystem.IsWindows()) return null;
        string path = Path.Combine(directory, "microblocks_qol_native.dll");
        return File.Exists(path) ? Path.GetFullPath(path) : null;
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

    [DllImport(LibraryName, EntryPoint = "mqol_recording_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RecordingFinalize(byte[] plan, nuint planLength);
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
