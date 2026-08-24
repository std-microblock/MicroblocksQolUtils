using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;

namespace Celeste.Mod.MicroblocksQolUtils;

public enum UiFontWeight {
    Regular,
    Bold
}

/// <summary>
/// Renders complete text runs through the portable Rust shaping and hinting
/// pipeline, then draws every texel at one physical screen pixel.
/// </summary>
public static class SystemTtfFont {
    private const float BasePixelSize = 42f;
    private const float BaseLineHeight = 54f;
    private const int MaxCachedRuns = 512;
    private const BindingFlags SpriteBatchFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo SpriteBatchBeginCalledField = RequiredSpriteBatchField("beginCalled", "_beginCalled");
    private static readonly FieldInfo SpriteBatchTransformMatrixField = RequiredSpriteBatchField(
        "transformMatrix", "_transformMatrix");
    private static readonly Dictionary<TextRunKey, TextRunCacheEntry> Runs = [];
    private static readonly LinkedList<TextRunKey> RunLru = [];
    private static string loadedIdentity = "";

    public static void Prepare() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        string identity = string.IsNullOrWhiteSpace(settings.FontFile)
            ? $"family:{settings.FontFamily.Trim()}"
            : $"file:{Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.FontFile.Trim()))}";
        if (loadedIdentity.Length > 0
            && string.Equals(identity, loadedIdentity, StringComparison.OrdinalIgnoreCase))
            return;

        Dispose();
        if (identity.StartsWith("file:", StringComparison.Ordinal)) {
            string path = identity[5..];
            if (!File.Exists(path)) throw new FileNotFoundException("Configured UI font does not exist.", path);
        }

        loadedIdentity = identity;
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils",
            $"Using portable swash UI rasterizer ({identity})");
    }

    public static Vector2 Measure(string text, float scale = 1f, UiFontWeight weight = UiFontWeight.Regular) =>
        MeasureMetrics(text, RasterContext.Create(scale), weight).LayoutSize;

    public static Vector2 MeasureVisible(
        string text,
        float scale = 1f,
        UiFontWeight weight = UiFontWeight.Regular
    ) => MeasureMetrics(text, RasterContext.Create(scale), weight).VisualBounds.Size;

    public static void Draw(
        string text,
        Vector2 position,
        Vector2 justify,
        float scale,
        Color color,
        float outline = 0f,
        Color? outlineColor = null,
        UiFontWeight weight = UiFontWeight.Regular
    ) {
        if (string.IsNullOrEmpty(text)) return;
        Prepare();
        RasterContext context = RasterContext.Create(scale);
        Vector2 origin = MeasureMetrics(text, context, weight).LayoutSize * justify;
        DrawWithOutline(text, position, origin, context, color, outline, outlineColor, weight);
    }

    public static void DrawVisual(
        string text,
        Vector2 position,
        Vector2 justify,
        float scale,
        Color color,
        float outline = 0f,
        Color? outlineColor = null,
        UiFontWeight weight = UiFontWeight.Regular
    ) {
        if (string.IsNullOrEmpty(text)) return;
        Prepare();
        RasterContext context = RasterContext.Create(scale);
        TextMetrics metrics = MeasureMetrics(text, context, weight);
        Vector2 origin = metrics.VisualBounds.Location + metrics.VisualBounds.Size * justify;
        DrawWithOutline(text, position, origin, context, color, outline, outlineColor, weight);
    }

    public static void Dispose() {
        foreach (TextRunCacheEntry entry in Runs.Values) entry.Run.Texture?.Dispose();
        Runs.Clear();
        RunLru.Clear();
        loadedIdentity = "";
    }

    private static void DrawWithOutline(
        string text,
        Vector2 position,
        Vector2 origin,
        RasterContext context,
        Color color,
        float outline,
        Color? outlineColor,
        UiFontWeight weight
    ) {
        if (outline > 0f) {
            Color stroke = outlineColor ?? Color.Black;
            DrawCore(text, position + new Vector2(-outline, 0f), origin, context, stroke, weight);
            DrawCore(text, position + new Vector2(outline, 0f), origin, context, stroke, weight);
            DrawCore(text, position + new Vector2(0f, -outline), origin, context, stroke, weight);
            DrawCore(text, position + new Vector2(0f, outline), origin, context, stroke, weight);
        }
        DrawCore(text, position, origin, context, color, weight);
    }

    private static void DrawCore(
        string text,
        Vector2 position,
        Vector2 origin,
        RasterContext context,
        Color color,
        UiFontWeight weight
    ) {
        TextRun run = GetRun(text, context, weight, color);
        if (run.Texture is null) return;
        Color tint = OpacityTint(color);
        Vector2 at = position + context.ToLayout(run.TextureOffsetPixels) - origin;
        at = context.SnapToPixel(at);
        Monocle.Draw.SpriteBatch.Draw(
            run.Texture,
            at,
            null,
            tint,
            0f,
            Vector2.Zero,
            context.TextureScale,
            SpriteEffects.None,
            0f
        );
    }

    private static TextMetrics MeasureMetrics(string text, RasterContext context, UiFontWeight weight) {
        if (string.IsNullOrEmpty(text)) return new TextMetrics(Vector2.Zero, FloatRect.Empty);
        TextRun run = GetRun(text, context, weight, Color.White);
        return new TextMetrics(
            context.ToLayout(run.LayoutPixels),
            context.ToLayout(run.VisualBoundsPixels)
        );
    }

    private static TextRun GetRun(string text, RasterContext context, UiFontWeight weight, Color color) {
        Prepare();
        BaseColor baseColor = GetBaseColor(color);
        TextRunKey key = new(text, context.PixelSize, context.LineHeightPixels, weight,
            baseColor.Red, baseColor.Green, baseColor.Blue);
        if (Runs.TryGetValue(key, out TextRunCacheEntry? cached)) {
            RunLru.Remove(cached.Node);
            RunLru.AddFirst(cached.Node);
            return cached.Run;
        }

        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        string fontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
            ? "Microsoft YaHei UI"
            : settings.FontFamily.Trim();
        string fontFile = string.IsNullOrWhiteSpace(settings.FontFile)
            ? ""
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.FontFile.Trim()));
        PortableTextRaster raster = PortableRasterizer.RasterizeText(
            text,
            fontFamily,
            fontFile,
            weight == UiFontWeight.Bold,
            context.PixelSize,
            context.LineHeightPixels,
            baseColor.Red,
            baseColor.Green,
            baseColor.Blue
        );
        Texture2D? texture = raster.Image is null ? null : CreateTexture(raster.Image);
        TextRun run = new(
            texture,
            new Vector2(raster.TextureOffsetX, raster.TextureOffsetY),
            new Vector2(raster.LayoutWidth, raster.LayoutHeight),
            new FloatRect(raster.VisualBounds.X, raster.VisualBounds.Y,
                raster.VisualBounds.Width, raster.VisualBounds.Height)
        );
        LinkedListNode<TextRunKey> node = RunLru.AddFirst(key);
        Runs.Add(key, new TextRunCacheEntry(run, node));
        while (Runs.Count > MaxCachedRuns && RunLru.Last is { } last) {
            RunLru.RemoveLast();
            if (Runs.Remove(last.Value, out TextRunCacheEntry? evicted)) evicted.Run.Texture?.Dispose();
        }
        return run;
    }

    private static Texture2D CreateTexture(PortableRasterImage image) {
        Color[] colors = new Color[image.Width * image.Height];
        for (int index = 0; index < colors.Length; index++) {
            int source = index * 4;
            colors[index] = new Color(
                image.BgraPremultiplied[source + 2],
                image.BgraPremultiplied[source + 1],
                image.BgraPremultiplied[source],
                image.BgraPremultiplied[source + 3]
            );
        }
        Texture2D texture = new(Engine.Graphics.GraphicsDevice, image.Width, image.Height);
        texture.SetData(colors);
        return texture;
    }

    private static BaseColor GetBaseColor(Color color) {
        if (color.A == 0) return new BaseColor(255, 255, 255);
        return new BaseColor(
            (byte)Math.Clamp((color.R * 255 + color.A / 2) / color.A, 0, 255),
            (byte)Math.Clamp((color.G * 255 + color.A / 2) / color.A, 0, 255),
            (byte)Math.Clamp((color.B * 255 + color.A / 2) / color.A, 0, 255)
        );
    }

    private static Color OpacityTint(Color color) {
        float opacity = color.A / 255f;
        return Color.White * opacity;
    }

    private static FieldInfo RequiredSpriteBatchField(params string[] names) {
        Type type = typeof(SpriteBatch);
        foreach (string name in names) {
            FieldInfo? field = type.GetField(name, SpriteBatchFields);
            if (field is not null) return field;
        }
        throw new MissingFieldException(type.FullName, string.Join(" or ", names));
    }

    private static XnaMatrix CurrentTransform() {
        try {
            SpriteBatch spriteBatch = Monocle.Draw.SpriteBatch;
            if ((bool?)SpriteBatchBeginCalledField.GetValue(spriteBatch) == true
                && SpriteBatchTransformMatrixField.GetValue(spriteBatch) is XnaMatrix transform)
                return transform;
        } catch {
            // Fall through to the normal high-resolution UI transform.
        }
        return HiresRenderer.DrawToBuffer ? XnaMatrix.Identity : Engine.ScreenMatrix;
    }

    private readonly record struct FloatRect(float X, float Y, float Width, float Height) {
        public static FloatRect Empty => new(0f, 0f, 0f, 0f);
        public Vector2 Location => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    private readonly record struct RasterContext(
        int PixelSize,
        int LineHeightPixels,
        Vector2 TextureScale,
        XnaMatrix Transform,
        XnaMatrix InverseTransform,
        bool ScreenAligned
    ) {
        public static RasterContext Create(float scale) {
            XnaMatrix transform = CurrentTransform();
            bool screenAligned = MathF.Abs(transform.M12) < 0.0001f
                && MathF.Abs(transform.M21) < 0.0001f
                && MathF.Abs(transform.M11) > 0.0001f
                && MathF.Abs(transform.M22) > 0.0001f;
            Vector2 pixelsPerUnit = screenAligned
                ? new Vector2(MathF.Abs(transform.M11), MathF.Abs(transform.M22))
                : Vector2.One;
            float uiScale = EarlyDpiBootstrap.UiScale;
            Vector2 textureScale = new(1f / pixelsPerUnit.X, 1f / pixelsPerUnit.Y);
            int pixelSize = Math.Max(8,
                (int)MathF.Round(BasePixelSize * Math.Max(0.01f, scale) * pixelsPerUnit.Y * uiScale));
            int lineHeightPixels = Math.Max(1,
                (int)MathF.Round(BaseLineHeight * Math.Max(0.01f, scale) * pixelsPerUnit.Y * uiScale));
            XnaMatrix inverse = screenAligned ? XnaMatrix.Invert(transform) : XnaMatrix.Identity;
            return new RasterContext(pixelSize, lineHeightPixels, textureScale,
                transform, inverse, screenAligned);
        }

        public Vector2 ToLayout(Vector2 pixels) => pixels * TextureScale;
        public FloatRect ToLayout(FloatRect pixels) => new(
            pixels.X * TextureScale.X,
            pixels.Y * TextureScale.Y,
            pixels.Width * TextureScale.X,
            pixels.Height * TextureScale.Y
        );

        public Vector2 SnapToPixel(Vector2 position) {
            if (!ScreenAligned) return new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
            Vector2 pixel = Vector2.Transform(position, Transform);
            pixel = new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
            return Vector2.Transform(pixel, InverseTransform);
        }
    }

    private readonly record struct TextRunKey(
        string Text,
        int PixelSize,
        int LineHeightPixels,
        UiFontWeight Weight,
        byte Red,
        byte Green,
        byte Blue
    );

    private readonly record struct BaseColor(byte Red, byte Green, byte Blue);

    private sealed record TextRun(
        Texture2D? Texture,
        Vector2 TextureOffsetPixels,
        Vector2 LayoutPixels,
        FloatRect VisualBoundsPixels
    );

    private sealed record TextRunCacheEntry(TextRun Run, LinkedListNode<TextRunKey> Node);
    private readonly record struct TextMetrics(Vector2 LayoutSize, FloatRect VisualBounds);
}
