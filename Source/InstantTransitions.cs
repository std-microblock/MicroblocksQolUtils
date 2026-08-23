using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class InstantTransitions {
    public static void Load() {
        On.Celeste.Level.TransitionTo += LevelTransitionTo;
        On.Celeste.Level.LoadLevel += LevelLoadLevel;
        On.Celeste.Player.TransitionTo += PlayerTransitionTo;
    }

    public static void Unload() {
        On.Celeste.Player.TransitionTo -= PlayerTransitionTo;
        On.Celeste.Level.LoadLevel -= LevelLoadLevel;
        On.Celeste.Level.TransitionTo -= LevelTransitionTo;
    }

    private static void LevelTransitionTo(
        On.Celeste.Level.orig_TransitionTo orig,
        Level self,
        LevelData next,
        Vector2 direction
    ) {
        if (MicroblocksQolUtilsModule.Settings.RemoveRoomTransitions) self.NextTransitionDuration = 0f;
        orig(self, next, direction);
    }

    private static void LevelLoadLevel(
        On.Celeste.Level.orig_LoadLevel orig,
        Level self,
        Player.IntroTypes intro,
        bool fromLoader
    ) {
        orig(self, intro, fromLoader);
        if (!MicroblocksQolUtilsModule.Settings.RemoveRoomTransitions || intro != Player.IntroTypes.Transition) return;
        self.NextTransitionDuration = 0f;
        self.Lighting.Alpha = self.DarkRoom
            ? self.Session.DarkRoomAlpha
            : self.BaseLightingAlpha + self.Session.LightingAlphaAdd;
    }

    private static bool PlayerTransitionTo(
        On.Celeste.Player.orig_TransitionTo orig,
        Player self,
        Vector2 target,
        Vector2 direction
    ) {
        if (MicroblocksQolUtilsModule.Settings.RemoveRoomTransitions) self.Position = target;
        return orig(self, target, direction);
    }
}

