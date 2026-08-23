using System.Collections;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class InstantDeaths {
    public static void Load() {
        On.Celeste.PlayerDeadBody.DeathRoutine += PlayerDeadBodyDeathRoutine;
    }

    public static void Unload() {
        On.Celeste.PlayerDeadBody.DeathRoutine -= PlayerDeadBodyDeathRoutine;
    }

    private static IEnumerator PlayerDeadBodyDeathRoutine(
        On.Celeste.PlayerDeadBody.orig_DeathRoutine orig,
        PlayerDeadBody self
    ) {
        return MicroblocksQolUtilsModule.Settings.RemoveDeathAnimation
            ? ReloadImmediately(self)
            : orig(self);
    }

    private static IEnumerator ReloadImmediately(PlayerDeadBody self) {
        Level? level;
        while ((level = self.Scene as Level) is null) yield return null;
        (self.DeathAction ?? level.Reload)();
    }
}
