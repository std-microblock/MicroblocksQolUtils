using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class MaterialAcrylicRenderer {
    private static VirtualRenderTarget? sceneTarget;
    private static VirtualRenderTarget? blurTemporary;
    private static VirtualRenderTarget? blurredTarget;
    private static bool rendering;
    private static bool failed;
    private static int successfulFrames;

    internal static bool Failed => failed;
    internal static int SuccessfulFrames => successfulFrames;

    public static void Load() {
        successfulFrames = 0;
        On.Monocle.Engine.RenderCore += RenderCore;
    }

    public static void Unload() {
        On.Monocle.Engine.RenderCore -= RenderCore;
        DisposeTargets();
        failed = false;
    }

    private static void RenderCore(On.Monocle.Engine.orig_RenderCore orig, Engine self) {
        IMaterialAcrylicPage? page = (IMaterialAcrylicPage?)MaterialChapterSelect.ActivePage
            ?? QolSettingsOverlay.ActivePage;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (rendering
            || failed
            || page is null
            || !settings.MaterialYouInterface
            || !settings.MaterialAcrylicBackground
            || Engine.Scene is not Scene scene) {
            orig(self);
            return;
        }

        rendering = true;
        GraphicsDevice graphics = self.GraphicsDevice;
        try {
            EnsureTargets(Math.Max(1, Engine.ViewWidth), Math.Max(1, Engine.ViewHeight));
            if (sceneTarget is null || blurTemporary is null || blurredTarget is null) {
                orig(self);
                return;
            }

            page.SuppressNormalRender = true;
            scene.BeforeRender();
            graphics.SetRenderTarget(sceneTarget);
            graphics.Viewport = new Viewport(0, 0, sceneTarget.Width, sceneTarget.Height);
            graphics.Clear(Engine.ClearColor);
            scene.Render();
            scene.AfterRender();
            page.SuppressNormalRender = false;

            float sampleScale = MathHelper.Lerp(0.8f, 3.1f,
                Math.Clamp(settings.MaterialAcrylicBlurStrength, 1, 12) / 12f);
            Texture2D blurred = GaussianBlur.Blur(
                sceneTarget,
                blurTemporary,
                blurredTarget,
                samples: GaussianBlur.Samples.Nine,
                sampleScale: sampleScale
            );

            graphics.SetRenderTarget(null);
            graphics.Viewport = Engine.Viewport;
            graphics.Clear(Engine.ClearColor);
            Draw.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.Opaque,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                RasterizerState.CullNone
            );
            Draw.SpriteBatch.Draw(blurred, new Rectangle(0, 0, Engine.ViewWidth, Engine.ViewHeight), Color.White);
            Draw.SpriteBatch.End();
            Draw.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                RasterizerState.CullNone
            );
            Draw.SpriteBatch.Draw(sceneTarget, new Rectangle(0, 0, Engine.ViewWidth, Engine.ViewHeight), Color.White * 0.22f);
            Draw.SpriteBatch.End();

            Draw.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                RasterizerState.CullNone,
                null,
                Engine.ScreenMatrix
            );
            page.RenderMaterialContent(acrylicActive: true);
            Draw.SpriteBatch.End();
            successfulFrames++;
        } catch (Exception exception) {
            page.SuppressNormalRender = false;
            graphics.SetRenderTarget(null);
            graphics.Viewport = Engine.Viewport;
            failed = true;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialAcrylic");
            orig(self);
        } finally {
            page.SuppressNormalRender = false;
            rendering = false;
        }
    }

    private static void EnsureTargets(int width, int height) {
        if (sceneTarget?.Width == width && sceneTarget.Height == height) return;
        DisposeTargets();
        sceneTarget = VirtualContent.CreateRenderTarget("mqol-material-scene", width, height);
        blurTemporary = VirtualContent.CreateRenderTarget("mqol-material-blur-a", width, height);
        blurredTarget = VirtualContent.CreateRenderTarget("mqol-material-blur-b", width, height);
    }

    private static void DisposeTargets() {
        sceneTarget?.Dispose();
        blurTemporary?.Dispose();
        blurredTarget?.Dispose();
        sceneTarget = null;
        blurTemporary = null;
        blurredTarget = null;
    }
}
