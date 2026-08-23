using Celeste;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class CollisionBoxRenderer {
    public static void Load() {
        IL.Celeste.GameplayRenderer.Render += GameplayRendererRender;
    }

    public static void Unload() {
        IL.Celeste.GameplayRenderer.Render -= GameplayRendererRender;
    }

    private static void GameplayRendererRender(ILContext il) {
        ILCursor cursor = new(il);
        ILLabel afterEntities = cursor.DefineLabel();

        if (!cursor.TryGotoNext(MoveType.After,
                instruction => instruction.MatchCallvirt<EntityList>(nameof(EntityList.RenderExcept)))) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils", "Could not install collision-box-only render hook");
            return;
        }
        cursor.MarkLabel(afterEntities);

        cursor.Index = 0;
        if (!cursor.TryGotoNext(MoveType.After,
                instruction => instruction.MatchCall(typeof(GameplayRenderer), nameof(GameplayRenderer.Begin)))) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils", "Could not install collision-box backdrop hook");
            return;
        }
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate(DrawOnlyModeBackdrop);
        cursor.EmitDelegate(OnlyCollisionBoxes);
        cursor.Emit(OpCodes.Brtrue, afterEntities);

        cursor.Index = 0;
        if (!cursor.TryGotoNext(MoveType.After,
                instruction => instruction.MatchLdsfld<GameplayRenderer>(nameof(GameplayRenderer.RenderDebug)))) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils", "Could not install collision-box display hook");
            return;
        }
        cursor.EmitDelegate(ShouldRenderCollisionBoxes);
    }

    private static void DrawOnlyModeBackdrop(GameplayRenderer renderer) {
        if (!OnlyCollisionBoxes()) return;

        Camera camera = renderer.Camera;
        Draw.Rect(
            camera.Left - 2f,
            camera.Top - 2f,
            camera.Viewport.Width + 4f,
            camera.Viewport.Height + 4f,
            Color.Black
        );
    }

    private static bool OnlyCollisionBoxes() =>
        MicroblocksQolUtilsModule.Settings.CollisionBoxes == CollisionBoxDisplayMode.Only;

    private static bool ShouldRenderCollisionBoxes(bool original) => original
        || MicroblocksQolUtilsModule.Settings.CollisionBoxes != CollisionBoxDisplayMode.Hidden;
}
