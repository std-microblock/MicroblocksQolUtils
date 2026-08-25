using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class InstantDeaths {
    private static PlayerDeadBody? reloadedBody;

    public static void AfterEngineUpdate() {
        if (!MicroblocksQolUtilsModule.Settings.RemoveDeathAnimation
            || Engine.Scene is not Level level) {
            reloadedBody = null;
            return;
        }

        PlayerDeadBody? body = level.Tracker.GetEntity<PlayerDeadBody>();
        if (body is null) {
            reloadedBody = null;
            return;
        }

        if (ReferenceEquals(body, reloadedBody)) return;
        reloadedBody = body;

        // DeathRoutine is commonly detoured by other mods, so replacing its enumerator is
        // not reliable. Run after the frame instead: the body is in the scene, and recorder
        // death handling has already completed before we start the reload.
        (body.DeathAction ?? level.Reload)();
    }

    public static void Reset() {
        reloadedBody = null;
    }
}
