using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal readonly record struct MaterialPalette(
    Color Scrim,
    Color Surface,
    Color SurfaceHigh,
    Color SurfaceHighest,
    Color Primary,
    Color OnPrimary,
    Color OnSurface,
    Color OnSurfaceVariant,
    Color Outline
) {
    public static MaterialPalette FromSeed(Color seed) {
        Color primary = Mix(seed, Color.White, 0.24f);
        Color surface = Mix(new Color(20, 18, 24), seed, 0.11f);
        return new MaterialPalette(
            new Color(8, 7, 11) * 0.48f,
            surface * 0.90f,
            Mix(surface, Color.White, 0.08f) * 0.94f,
            Mix(surface, Color.White, 0.14f) * 0.97f,
            primary,
            Luminance(primary) > 0.58f ? new Color(28, 24, 31) : Color.White,
            new Color(245, 239, 249),
            new Color(205, 195, 211),
            new Color(146, 136, 151) * 0.72f
        );
    }

    private static Color Mix(Color from, Color to, float amount) => new(
        (byte)MathHelper.Lerp(from.R, to.R, amount),
        (byte)MathHelper.Lerp(from.G, to.G, amount),
        (byte)MathHelper.Lerp(from.B, to.B, amount),
        (byte)MathHelper.Lerp(from.A, to.A, amount)
    );

    private static float Luminance(Color value) =>
        (value.R * 0.2126f + value.G * 0.7152f + value.B * 0.0722f) / 255f;
}

internal static class MaterialUi {
    private static readonly Dictionary<(int Width, int Height, int Radius), Texture2D> RoundedMasks = [];
    private static readonly Dictionary<(int Width, int Height, int Radius, int Thickness), Texture2D> OutlineMasks = [];
    private static readonly Dictionary<int, Texture2D> CircleMasks = [];
    private static readonly Dictionary<(int Diameter, int Thickness), Texture2D> CircleOutlineMasks = [];
    private static readonly Dictionary<(int Length, int ThicknessQuarterPixels), Texture2D> LineMasks = [];
    private const int CoverageSamples = 4;

    public static void RoundedRect(float x, float y, float width, float height, float radius, Color color) {
        int pixelWidth = Math.Max(1, (int)MathF.Round(width));
        int pixelHeight = Math.Max(1, (int)MathF.Round(height));
        int pixelRadius = Math.Clamp((int)MathF.Round(radius), 0, Math.Min(pixelWidth, pixelHeight) / 2);
        Texture2D mask = GetRoundedMask(pixelWidth, pixelHeight, pixelRadius);
        Draw.SpriteBatch.Draw(mask, new Rectangle((int)x, (int)y, pixelWidth, pixelHeight), color);
    }

    public static void RoundedOutline(
        float x,
        float y,
        float width,
        float height,
        float radius,
        float thickness,
        Color color
    ) {
        int pixelWidth = Math.Max(1, (int)MathF.Round(width));
        int pixelHeight = Math.Max(1, (int)MathF.Round(height));
        int pixelRadius = Math.Clamp((int)MathF.Round(radius), 0, Math.Min(pixelWidth, pixelHeight) / 2);
        int pixelThickness = Math.Clamp((int)MathF.Round(thickness), 1, Math.Min(pixelWidth, pixelHeight) / 2);
        Texture2D mask = GetOutlineMask(pixelWidth, pixelHeight, pixelRadius, pixelThickness);
        Draw.SpriteBatch.Draw(mask, new Rectangle((int)x, (int)y, pixelWidth, pixelHeight), color);
    }

    public static void Circle(Vector2 center, float radius, Color color) {
        int diameter = Math.Max(1, (int)MathF.Round(radius * 2f));
        Texture2D mask = GetCircleMask(diameter);
        Draw.SpriteBatch.Draw(mask, center - new Vector2(diameter / 2f), color);
    }

    public static void CircleOutline(Vector2 center, float radius, float thickness, Color color) {
        int diameter = Math.Max(1, (int)MathF.Round(radius * 2f));
        int pixelThickness = Math.Clamp((int)MathF.Round(thickness), 1, Math.Max(1, diameter / 2));
        Texture2D mask = GetCircleOutlineMask(diameter, pixelThickness);
        Draw.SpriteBatch.Draw(mask, center - new Vector2(diameter / 2f), color);
    }

    public static void Line(Vector2 from, Vector2 to, float thickness, Color color) {
        Vector2 delta = to - from;
        float length = delta.Length();
        if (length < 0.001f) {
            Circle(from, Math.Max(0.5f, thickness / 2f), color);
            return;
        }
        int pixelLength = Math.Max(1, (int)MathF.Round(length));
        int thicknessQuarterPixels = Math.Max(1, (int)MathF.Round(thickness * 4f));
        Texture2D mask = GetLineMask(pixelLength, thicknessQuarterPixels);
        Draw.SpriteBatch.Draw(
            mask,
            (from + to) / 2f,
            null,
            color,
            MathF.Atan2(delta.Y, delta.X),
            new Vector2(mask.Width / 2f, mask.Height / 2f),
            Vector2.One,
            SpriteEffects.None,
            0f
        );
    }

    public static void AcrylicSurface(
        float x,
        float y,
        float width,
        float height,
        float radius,
        Color tint,
        Color outline
    ) {
        RoundedRect(x, y + 8f, width, height, radius, Color.Black * 0.24f);
        RoundedRect(x, y, width, height, radius, tint);
        RoundedOutline(x, y, width, height, radius, 2f, outline);
    }

    public static bool Contains(Vector2 point, float x, float y, float width, float height) =>
        point.X >= x && point.X <= x + width && point.Y >= y && point.Y <= y + height;

    public static void Dispose() {
        foreach (Texture2D texture in RoundedMasks.Values) texture.Dispose();
        RoundedMasks.Clear();
        foreach (Texture2D texture in OutlineMasks.Values) texture.Dispose();
        OutlineMasks.Clear();
        foreach (Texture2D texture in CircleMasks.Values) texture.Dispose();
        CircleMasks.Clear();
        foreach (Texture2D texture in CircleOutlineMasks.Values) texture.Dispose();
        CircleOutlineMasks.Clear();
        foreach (Texture2D texture in LineMasks.Values) texture.Dispose();
        LineMasks.Clear();
    }

    private static Texture2D GetRoundedMask(int width, int height, int radius) {
        var key = (width, height, radius);
        if (RoundedMasks.TryGetValue(key, out Texture2D? texture)) return texture;
        Color[] pixels = new Color[width * height];
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                float coverage = RoundedCoverage(x, y, width, height, radius);
                pixels[y * width + x] = PremultipliedWhite(coverage);
            }
        }
        texture = new Texture2D(Engine.Graphics.GraphicsDevice, width, height);
        texture.SetData(pixels);
        RoundedMasks[key] = texture;
        return texture;
    }

    private static Texture2D GetOutlineMask(int width, int height, int radius, int thickness) {
        var key = (width, height, radius, thickness);
        if (OutlineMasks.TryGetValue(key, out Texture2D? texture)) return texture;
        Color[] pixels = new Color[width * height];
        int innerWidth = Math.Max(0, width - thickness * 2);
        int innerHeight = Math.Max(0, height - thickness * 2);
        int innerRadius = Math.Max(0, radius - thickness);
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                float outer = RoundedCoverage(x, y, width, height, radius);
                float inner = innerWidth > 0 && innerHeight > 0
                    ? RoundedCoverage(x - thickness, y - thickness, innerWidth, innerHeight, innerRadius)
                    : 0f;
                pixels[y * width + x] = PremultipliedWhite(Math.Clamp(outer - inner, 0f, 1f));
            }
        }
        texture = new Texture2D(Engine.Graphics.GraphicsDevice, width, height);
        texture.SetData(pixels);
        OutlineMasks[key] = texture;
        return texture;
    }

    private static float RoundedCoverage(int pixelX, int pixelY, int width, int height, int radius) {
        int hits = 0;
        for (int sampleY = 0; sampleY < CoverageSamples; sampleY++) {
            for (int sampleX = 0; sampleX < CoverageSamples; sampleX++) {
                float x = pixelX + (sampleX + 0.5f) / CoverageSamples;
                float y = pixelY + (sampleY + 0.5f) / CoverageSamples;
                if (InsideRounded(x, y, width, height, radius)) hits++;
            }
        }
        return hits / (float)(CoverageSamples * CoverageSamples);
    }

    private static bool InsideRounded(float x, float y, int width, int height, int radius) {
        if (x < 0f || y < 0f || x >= width || y >= height) return false;
        if (radius <= 0) return true;
        float left = radius;
        float right = width - radius;
        float top = radius;
        float bottom = height - radius;
        float nearestX = Math.Clamp(x, left, right);
        float nearestY = Math.Clamp(y, top, bottom);
        float dx = x - nearestX;
        float dy = y - nearestY;
        return dx * dx + dy * dy <= radius * radius;
    }

    private static Texture2D GetCircleMask(int diameter) {
        if (CircleMasks.TryGetValue(diameter, out Texture2D? texture)) return texture;
        Color[] pixels = new Color[diameter * diameter];
        float radius = diameter / 2f;
        for (int y = 0; y < diameter; y++) {
            for (int x = 0; x < diameter; x++) {
                float coverage = CircleCoverage(x, y, radius, 0f);
                pixels[y * diameter + x] = PremultipliedWhite(coverage);
            }
        }
        texture = new Texture2D(Engine.Graphics.GraphicsDevice, diameter, diameter);
        texture.SetData(pixels);
        CircleMasks[diameter] = texture;
        return texture;
    }

    private static Texture2D GetCircleOutlineMask(int diameter, int thickness) {
        var key = (diameter, thickness);
        if (CircleOutlineMasks.TryGetValue(key, out Texture2D? texture)) return texture;
        Color[] pixels = new Color[diameter * diameter];
        float radius = diameter / 2f;
        for (int y = 0; y < diameter; y++) {
            for (int x = 0; x < diameter; x++) {
                float coverage = CircleCoverage(x, y, radius, thickness);
                pixels[y * diameter + x] = PremultipliedWhite(coverage);
            }
        }
        texture = new Texture2D(Engine.Graphics.GraphicsDevice, diameter, diameter);
        texture.SetData(pixels);
        CircleOutlineMasks[key] = texture;
        return texture;
    }

    private static float CircleCoverage(int pixelX, int pixelY, float radius, float thickness) {
        int hits = 0;
        float inner = Math.Max(0f, radius - thickness);
        float center = radius;
        for (int sampleY = 0; sampleY < CoverageSamples; sampleY++) {
            for (int sampleX = 0; sampleX < CoverageSamples; sampleX++) {
                float x = pixelX + (sampleX + 0.5f) / CoverageSamples - center;
                float y = pixelY + (sampleY + 0.5f) / CoverageSamples - center;
                float lengthSquared = x * x + y * y;
                if (lengthSquared <= radius * radius
                    && (thickness <= 0f || lengthSquared >= inner * inner)) hits++;
            }
        }
        return hits / (float)(CoverageSamples * CoverageSamples);
    }

    private static Texture2D GetLineMask(int length, int thicknessQuarterPixels) {
        var key = (length, thicknessQuarterPixels);
        if (LineMasks.TryGetValue(key, out Texture2D? texture)) return texture;
        const int padding = 2;
        float thickness = thicknessQuarterPixels / 4f;
        float radius = thickness / 2f;
        int width = length + padding * 2;
        int height = Math.Max(1, (int)MathF.Ceiling(thickness)) + padding * 2;
        float centerY = height / 2f;
        Color[] pixels = new Color[width * height];
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int hits = 0;
                for (int sampleY = 0; sampleY < CoverageSamples; sampleY++) {
                    for (int sampleX = 0; sampleX < CoverageSamples; sampleX++) {
                        float px = x + (sampleX + 0.5f) / CoverageSamples;
                        float py = y + (sampleY + 0.5f) / CoverageSamples;
                        float nearestX = Math.Clamp(px, padding, padding + length);
                        float dx = px - nearestX;
                        float dy = py - centerY;
                        if (dx * dx + dy * dy <= radius * radius) hits++;
                    }
                }
                pixels[y * width + x] = PremultipliedWhite(
                    hits / (float)(CoverageSamples * CoverageSamples)
                );
            }
        }
        texture = new Texture2D(Engine.Graphics.GraphicsDevice, width, height);
        texture.SetData(pixels);
        LineMasks[key] = texture;
        return texture;
    }

    private static Color PremultipliedWhite(float coverage) {
        byte alpha = (byte)Math.Clamp((int)MathF.Round(coverage * 255f), 0, 255);
        return new Color(alpha, alpha, alpha, alpha);
    }

}
