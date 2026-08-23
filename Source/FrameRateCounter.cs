using System.Diagnostics;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class FrameRateCounter {
    private static readonly RateWindow Physics = new();
    private static readonly RateWindow Render = new();

    public static float PhysicsFps { get; private set; }
    public static float RenderFps { get; private set; }

    public static void TickUpdate() {
        if (Physics.Tick() is float fps) PhysicsFps = fps;
    }

    public static void TickRender() {
        if (Render.Tick() is float fps) RenderFps = fps;
    }

    public static void Reset() {
        Physics.Reset();
        Render.Reset();
        PhysicsFps = 0f;
        RenderFps = 0f;
    }

    private sealed class RateWindow {
        private long windowStart = Stopwatch.GetTimestamp();
        private int ticks;

        public float? Tick() {
            ticks++;
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - windowStart) / (double)Stopwatch.Frequency;
            if (seconds < 0.5) return null;

            float rate = (float)(ticks / seconds);
            ticks = 0;
            windowStart = now;
            return rate;
        }

        public void Reset() {
            ticks = 0;
            windowStart = Stopwatch.GetTimestamp();
        }
    }
}
