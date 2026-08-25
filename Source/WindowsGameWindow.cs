using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class WindowsGameWindow {
    private static nint cachedHandle;

    internal static nint NativeHandle {
        get {
            if (!OperatingSystem.IsWindows()) return nint.Zero;
            if (cachedHandle != nint.Zero && IsWindow(cachedHandle)) return cachedHandle;

            using Process process = Process.GetCurrentProcess();
            cachedHandle = process.MainWindowHandle;
            return cachedHandle;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);
}
