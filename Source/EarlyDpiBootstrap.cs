using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Runs when Everest first loads the mod assembly, before EverestModule.Load.
/// On Windows this opts the process into physical-pixel rendering before FNA
/// creates its game window. Other platforms keep their native DPI behavior.
/// </summary>
internal static class EarlyDpiBootstrap {
    private static readonly nint PerMonitorAwareV2 = new(-4);
    private static bool windowScaleApplied;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "The mod assembly is loaded before FNA creates the game window.")]
    internal static void Initialize() {
        if (!OperatingSystem.IsWindows()) return;

        try {
            Environment.SetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI", "1");
            Environment.SetEnvironmentVariable("SDL_VIDEO_HIGHDPI_DISABLED", "0");
            Environment.SetEnvironmentVariable("SDL_WINDOWS_DPI_AWARENESS", "permonitorv2");
            _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        } catch {
            // Failing to opt into physical DPI must not prevent the mod from loading.
        }
    }

    internal static void ApplyWindowScale() {
        if (!OperatingSystem.IsWindows() || windowScaleApplied) return;

        nint window = Process.GetCurrentProcess().MainWindowHandle;
        if (window == nint.Zero) return;
        if (GetAwarenessFromDpiAwarenessContext(
            GetWindowDpiAwarenessContext(window)) != DpiAwareness.PerMonitorAware) {
            windowScaleApplied = true;
            return;
        }
        if (global::Celeste.Settings.Instance.Fullscreen) {
            windowScaleApplied = true;
            return;
        }

        uint dpi = GetDpiForWindow(window);
        if (dpi <= 96) {
            windowScaleApplied = true;
            return;
        }

        int current = global::Celeste.Settings.Instance.WindowScale;
        int displayLimit = PhysicalDisplayScaleLimit(window);
        int migrated = Math.Clamp((int)MathF.Round(current * dpi / 96f), 1, displayLimit);
        windowScaleApplied = true;
        if (migrated == current) return;

        Engine.SetWindowed(migrated * 320, migrated * 180);
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils/DPI",
            $"Enabled physical-pixel rendering at {dpi} DPI and scaled the window {current} -> {migrated} without changing Celeste settings.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll")]
    private static extern DpiAwareness GetAwarenessFromDpiAwarenessContext(nint context);

    private static int PhysicalDisplayScaleLimit(nint window) {
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        nint monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info)) {
            return Math.Max(1, Math.Min(
                (info.Monitor.Right - info.Monitor.Left) / 320,
                (info.Monitor.Bottom - info.Monitor.Top) / 180
            ));
        }
        return 16;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private enum DpiAwareness {
        Invalid = -1,
        Unaware = 0,
        SystemAware = 1,
        PerMonitorAware = 2
    }
}
