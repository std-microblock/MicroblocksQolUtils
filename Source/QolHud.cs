using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

[Tracked]
public sealed class QolHud : Entity {
    public QolHud() {
        Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate | Tags.TransitionUpdate;
        Depth = -1_000_000;
    }

    public override void Update() {
        base.Update();
        if (!MicroblocksQolUtilsModule.Settings.Enabled) return;
        MiaoNetBridge.Update(Scene as Level);
        if (Scene is Level level) {
            UpdateMiniMapZoom();
            AutoRecorder.Update(level);
        }
    }

    public override void Render() {
        base.Render();
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.Enabled) return;

        if (Scene is Level level) MiniMapRenderer.Render(level);
        RecordingHudRenderer.Render();

        if (settings.ShowFps) {
            bool dualFps = settings.ShowPhysicalAndRenderFps && MotionSmoothingBridge.Enabled;
            string text = dualFps
                ? $"物理 {FrameRateCounter.PhysicsFps,3:0} FPS  ·  渲染 {FrameRateCounter.RenderFps,3:0} FPS"
                : $"{FrameRateCounter.RenderFps,3:0} FPS";
            if (settings.ShowFrameTime)
                text += $"  ·  {FrameProfiler.LastFrameMilliseconds,5:0.0} ms CPU";
            Vector2 position = new(18f, 16f);
            MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
            Vector2 measured = SystemTtfFont.Measure(text, 0.43f);
            if (settings.HudMaterialSurfaces) {
                MaterialUi.AcrylicSurface(
                    position.X - 10f,
                    position.Y - 7f,
                    measured.X + 20f,
                    measured.Y + 14f,
                    16f,
                    palette.SurfaceHigh * 0.90f,
                    palette.Outline
                );
            }
            SystemTtfFont.Draw(text, position, Vector2.Zero, 0.43f, palette.OnSurface, 0f);
            if (settings.EnableFrameProfiler) FrameProfiler.RenderHud(new Vector2(18f, 48f));
        }
    }

    private static void UpdateMiniMapZoom() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (settings.MiniMapZoomInBinding.Pressed)
            settings.MiniMapZoom = Math.Min(12, settings.MiniMapZoom + 1);
        if (settings.MiniMapZoomOutBinding.Pressed)
            settings.MiniMapZoom = Math.Max(0, settings.MiniMapZoom - 1);
    }
}
