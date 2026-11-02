namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Turns a held key into the keyboard repeat pattern native text boxes use: one action on the
/// press, then <see cref="InitialDelay"/> seconds later another every <see cref="Interval"/>
/// seconds. Feed it once per frame with the raw key state.
/// </summary>
internal sealed class TextKeyRepeat {
    public const float InitialDelay = 0.40f;
    public const float Interval = 0.045f;

    private bool armed;
    private float held;
    private float threshold = InitialDelay;

    public void Reset() {
        armed = false;
        held = 0f;
        threshold = InitialDelay;
    }

    /// <summary>Returns true when the key should act this frame (press or a repeat tick).</summary>
    public bool Update(bool down, float delta) {
        if (!down) {
            Reset();
            return false;
        }
        if (!armed) {
            armed = true;
            held = 0f;
            threshold = InitialDelay;
            return true;
        }
        held += Math.Max(0f, delta);
        if (held < threshold) return false;
        // One action per frame even when the frame was long: the caret should not jump a
        // whole paragraph after a hitch.
        held = 0f;
        threshold = Interval;
        return true;
    }
}
