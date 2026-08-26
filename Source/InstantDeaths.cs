using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class InstantDeaths {
    private static readonly System.Reflection.MethodInfo? EndMethod = typeof(PlayerDeadBody).GetMethod(
        "End",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
    );
    private static PlayerDeadBody? reloadedBody;

    public static void AfterEngineUpdate() {
        if (!MicroblocksQolUtilsModule.Settings.RemoveDeathAnimation
            || Engine.Scene is not Level level) {
            reloadedBody = null;
            return;
        }

        // PlayerDeadBody is not tracked by vanilla Monocle. Ask Everest to register the
        // type on demand instead of indexing Tracker.Entities through GetEntity<T>().
        PlayerDeadBody? body = level.Tracker
            .GetEntitiesTrackIfNeeded<PlayerDeadBody>()
            .FirstOrDefault() as PlayerDeadBody;
        if (body is null) {
            reloadedBody = null;
            return;
        }

        if (ReferenceEquals(body, reloadedBody)) return;
        reloadedBody = body;

        // DeathRoutine is commonly detoured by other mods, so replacing its enumerator is
        // not reliable. Run after the frame instead: the body is in the scene, and recorder
        // death handling has already completed before we finish the death. Invoke End rather
        // than DeathAction so hooks such as SpeedrunTool's auto-load-after-death logic still run.
        if (EndMethod is not null) EndMethod.Invoke(body, null);
        else (body.DeathAction ?? level.Reload)();
    }

    public static void Reset() {
        reloadedBody = null;
    }
}
