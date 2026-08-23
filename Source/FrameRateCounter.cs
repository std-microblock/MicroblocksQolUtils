using System.Diagnostics;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class FrameRateCounter {
    private static long windowStart = Stopwatch.GetTimestamp();
    private static int updates;
    private static int renders;

    public static float PhysicsFps { get; private set; }
    public static float RenderFps { get; private set; }

    public static void TickUpdate() {
        updates++;
        RefreshIfNeeded();
    }

    public static void TickRender() {
        renders++;
        RefreshIfNeeded();
    }

    private static void RefreshIfNeeded() {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
        if (seconds < 0.5) return;
        PhysicsFps = (float)(updates / seconds);
        RenderFps = (float)(renders / seconds);
        updates = 0;
        renders = 0;
        windowStart = now;
    }
}
