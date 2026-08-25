using System.Runtime.InteropServices;
using Monocle;

#pragma warning disable CA1416

namespace Celeste.Mod.MicroblocksQolUtils;

public static class WindowsNotifier {
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint IconId = 0x4D514F4C;

    public static bool IsGameForeground() {
        if (!OperatingSystem.IsWindows()) return true;

        // FNA already tracks the window activation state from SDL events. Reading it avoids
        // asking Windows to enumerate the process' top-level windows every update frame.
        return Engine.Instance?.IsActive ?? true;
    }

    public static void Show(string title, string message) {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr window = WindowsGameWindow.NativeHandle;

        NotifyIconData data = new() {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = window,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip | NifInfo,
            hIcon = System.Drawing.SystemIcons.Information.Handle,
            szTip = "microblock's QoL Utils",
            szInfoTitle = Limit(title, 63),
            szInfo = Limit(message, 255),
            dwInfoFlags = NiifInfo,
            uTimeoutOrVersion = 7000
        };
        Shell_NotifyIcon(NimDelete, ref data);
        if (!Shell_NotifyIcon(NimAdd, ref data)) return;
        Shell_NotifyIcon(NimModify, ref data);
        _ = Task.Run(async () => {
            await Task.Delay(8000).ConfigureAwait(false);
            Shell_NotifyIcon(NimDelete, ref data);
        });
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
}
