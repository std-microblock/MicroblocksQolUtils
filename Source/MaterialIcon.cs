using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Rasterizes embedded Material Symbols with the portable Rust vector path
/// rasterizer at their final physical size, then uploads and draws 1:1.
/// </summary>
internal static class MaterialIcon {
    private const string ResourcePrefix = "Celeste.Mod.MicroblocksQolUtils.MaterialSymbols.Rounded.";
    private const BindingFlags SpriteBatchFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo SpriteBatchBeginCalledField = RequiredSpriteBatchField(
        "beginCalled", "_beginCalled");
    private static readonly FieldInfo SpriteBatchTransformMatrixField = RequiredSpriteBatchField(
        "transformMatrix", "_transformMatrix");
    private static readonly Dictionary<(string Name, int PixelSize, byte Red, byte Green, byte Blue), Texture2D?> Textures = [];
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);

    public static void Draw(string name, Vector2 center, float size, Color color, float alpha = 1f) {
        if (size <= 0f || alpha <= 0f) return;
        RasterContext context = RasterContext.Create(size);
        Color finalColor = color * alpha;
        BaseColor baseColor = GetBaseColor(finalColor);
        Texture2D? texture = GetTexture(name, context.PixelSize, baseColor);
        if (texture is null) return;
        Vector2 at = context.SnapToPixel(center - context.LayoutSize / 2f);
        Monocle.Draw.SpriteBatch.Draw(
            texture,
            at,
            null,
            Color.White * (finalColor.A / 255f),
            0f,
            Vector2.Zero,
            context.TextureScale,
            SpriteEffects.None,
            0f
        );
    }

    public static bool Exists(string name) =>
        typeof(MaterialIcon).Assembly.GetManifestResourceInfo(ResourceName(name)) is not null;

    public static void Dispose() {
        foreach (Texture2D? texture in Textures.Values) texture?.Dispose();
        Textures.Clear();
        ReportedFailures.Clear();
    }

    private static Texture2D? GetTexture(string name, int pixelSize, BaseColor color) {
        name = NormalizeName(name);
        if (name.Length == 0) return null;
        var key = (name, pixelSize, color.Red, color.Green, color.Blue);
        if (Textures.TryGetValue(key, out Texture2D? texture)) return texture;
        try {
            Assembly assembly = typeof(MaterialIcon).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(ResourceName(name))
                ?? throw new FileNotFoundException($"Embedded Material Symbol '{name}' was not found.");
            return Textures[key] = CreateTexture(PortableRasterizer.RasterizeSvg(
                stream, pixelSize, color.Red, color.Green, color.Blue));
        } catch (Exception exception) {
            if (ReportedFailures.Add(name)) {
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/MaterialIcon",
                    $"Could not load Material Symbol '{name}': {exception.Message}");
            }
            return Textures[key] = null;
        }
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

    private static string NormalizeName(string name) => name.Trim().Replace(' ', '_');
    private static string ResourceName(string name) => ResourcePrefix + NormalizeName(name) + ".svg";

    private readonly record struct BaseColor(byte Red, byte Green, byte Blue);

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

    private readonly record struct RasterContext(
        int PixelSize,
        Vector2 LayoutSize,
        Vector2 TextureScale,
        XnaMatrix Transform,
        XnaMatrix InverseTransform,
        bool ScreenAligned
    ) {
        public static RasterContext Create(float size) {
            XnaMatrix transform = CurrentTransform();
            bool screenAligned = MathF.Abs(transform.M12) < 0.0001f
                && MathF.Abs(transform.M21) < 0.0001f
                && MathF.Abs(transform.M11) > 0.0001f
                && MathF.Abs(transform.M22) > 0.0001f;
            Vector2 pixelsPerUnit = screenAligned
                ? new Vector2(MathF.Abs(transform.M11), MathF.Abs(transform.M22))
                : Vector2.One;
            int pixelSize = Math.Max(1, (int)MathF.Round(size * pixelsPerUnit.Y));
            Vector2 textureScale = new(1f / pixelsPerUnit.X, 1f / pixelsPerUnit.Y);
            XnaMatrix inverse = screenAligned ? XnaMatrix.Invert(transform) : XnaMatrix.Identity;
            return new RasterContext(pixelSize, new Vector2(pixelSize) * textureScale,
                textureScale, transform, inverse, screenAligned);
        }

        public Vector2 SnapToPixel(Vector2 position) {
            if (!ScreenAligned) return new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
            Vector2 pixel = Vector2.Transform(position, Transform);
            pixel = new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
            return Vector2.Transform(pixel, InverseTransform);
        }
    }
}
