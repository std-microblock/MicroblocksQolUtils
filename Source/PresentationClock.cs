namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Timestamps presented frames by the fixed-step game content they show, not by the
/// wall-clock moment Present happened to be called.
///
/// FNA runs Celeste on a fixed 60-Hz step and catches up after a late tick by running
/// several updates before one draw. The CPU-side Present call time therefore jitters
/// against the content: a frame carrying two updates can arrive 16 ms after the
/// previous one and a frame carrying one update 33 ms after it. Quantizing those wall
/// times to the 60-FPS output grid both collided frames (dropped as duplicates) and
/// skewed their spacing, so recordings alternated between fast and slow motion.
///
/// Content time is <c>anchor + updates * step</c>. The anchor is snapped to the 60-Hz
/// output grid and re-anchored to the wall clock only when they diverge by more than
/// <see cref="DriftLimitNanos"/>, which keeps audio (recorded on the wall clock) in
/// sync during genuine slowdowns. Without fixed-step content (no update reported, or
/// MotionSmoothing interpolating decoupled renders) wall time is used unchanged.
/// </summary>
internal sealed class PresentationClock {
    internal const long DriftLimitNanos = 50_000_000;
    private const ulong GridRate = 60;

    internal static readonly PresentationClock Shared = new();

    private long updates;
    private bool fixedStep;
    private ulong stepNanos;
    private bool anchored;
    private long anchorUpdates;
    private ulong anchorNanos;
    private ulong last;

    internal long Resyncs { get; private set; }

    /// <summary>Called once per game update (Engine.Update), on the game thread.</summary>
    internal void Advance(bool contentFollowsUpdates, long targetElapsedTicks) {
        updates++;
        ulong step = targetElapsedTicks > 0 ? (ulong)targetElapsedTicks * 100 : 0;
        if (!contentFollowsUpdates || step == 0 || step != stepNanos) anchored = false;
        fixedStep = contentFollowsUpdates && step != 0;
        stepNanos = step;
    }

    /// <summary>Timestamp for a frame presented now; strictly increasing.</summary>
    internal ulong Timestamp(ulong wall) {
        ulong value = wall;
        if (fixedStep) {
            if (!anchored) Anchor(wall);
            value = anchorNanos + (ulong)(updates - anchorUpdates) * stepNanos;
            long drift = value >= wall ? (long)Math.Min(value - wall, long.MaxValue) : -(long)Math.Min(wall - value, long.MaxValue);
            if (Math.Abs(drift) > DriftLimitNanos) {
                Resyncs++;
                Anchor(wall);
                value = anchorNanos;
            }
        }
        if (value <= last) value = last + 1;
        last = value;
        return value;
    }

    internal void Reset() {
        updates = 0; fixedStep = false; stepNanos = 0; anchored = false;
        anchorUpdates = 0; anchorNanos = 0; last = 0; Resyncs = 0;
    }

    private void Anchor(ulong wall) {
        // Center the content grid on the encoder's rounded 60-Hz ticks so per-update
        // timestamps never straddle a rounding boundary.
        UInt128 tick = ((UInt128)wall * GridRate + 500_000_000) / 1_000_000_000;
        anchorNanos = (ulong)((tick * 1_000_000_000 + GridRate - 1) / GridRate);
        anchorUpdates = updates;
        anchored = true;
    }
}
