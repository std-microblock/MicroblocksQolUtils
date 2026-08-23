using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingFont = System.Drawing.Font;
using DrawingFontFamily = System.Drawing.FontFamily;
using DrawingFontStyle = System.Drawing.FontStyle;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsUnit = System.Drawing.GraphicsUnit;
using DrawingPointF = System.Drawing.PointF;
using DrawingStringFormat = System.Drawing.StringFormat;
using DrawingStringFormatFlags = System.Drawing.StringFormatFlags;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;

#pragma warning disable CA1416

namespace Celeste.Mod.MicroblocksQolUtils;

public enum UiFontWeight {
    Regular,
    Bold
}

/// <summary>
/// Rasterizes glyphs directly from a Windows-installed font or an arbitrary
/// user-supplied TTF/OTF. Glyph textures are created lazily and retained on the
/// GPU; no Celeste bitmap-font atlas is required.
/// </summary>
public static class SystemTtfFont {
    private const float BasePixelSize = 42f;
    private const float BaseLineHeight = 54f;
    private const int ClearTypeCoverageBoost = 302;
    private static readonly Dictionary<(char Character, int PixelSize, UiFontWeight Weight), Glyph> Glyphs = [];
    private static readonly Dictionary<(int PixelSize, UiFontWeight Weight), DrawingFont> Fonts = [];
    private const BindingFlags SpriteBatchFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo SpriteBatchBeginCalledField = RequiredSpriteBatchField("beginCalled", "_beginCalled");
    private static readonly FieldInfo SpriteBatchTransformMatrixField = RequiredSpriteBatchField(
        "transformMatrix", "_transformMatrix");
    private static PrivateFontCollection? privateFonts;
    private static DrawingFontFamily? fontFamily;
    private static DrawingStringFormat? stringFormat;
    private static string loadedIdentity = "";

    public static void Prepare() {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("System TTF rendering currently supports Windows only.");

        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        string identity = string.IsNullOrWhiteSpace(settings.FontFile)
            ? $"family:{settings.FontFamily.Trim()}"
            : $"file:{Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.FontFile.Trim()))}";
        if (fontFamily is not null && string.Equals(identity, loadedIdentity, StringComparison.OrdinalIgnoreCase)) return;

        Dispose();
        if (identity.StartsWith("file:", StringComparison.Ordinal)) {
            string path = identity[5..];
            if (!File.Exists(path)) throw new FileNotFoundException("Configured UI font does not exist.", path);
            privateFonts = new PrivateFontCollection();
            privateFonts.AddFontFile(path);
            if (privateFonts.Families.Length == 0) throw new InvalidDataException($"No font family found in {path}");
            fontFamily = privateFonts.Families[0];
        } else {
            string familyName = settings.FontFamily.Trim();
            if (familyName.Length == 0) familyName = "Microsoft YaHei UI";
            fontFamily = new DrawingFontFamily(familyName);
        }

        stringFormat = (DrawingStringFormat)DrawingStringFormat.GenericTypographic.Clone();
        stringFormat.FormatFlags |= DrawingStringFormatFlags.MeasureTrailingSpaces;
        loadedIdentity = identity;
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", $"Using system UI font {fontFamily.Name} ({identity})");
    }

    public static Vector2 Measure(string text, float scale = 1f, UiFontWeight weight = UiFontWeight.Regular) {
        return MeasureMetrics(text, RasterContext.Create(scale), weight).LayoutSize;
    }

    /// <summary>
    /// Returns the visible ink size rather than the font's full line box. This is
    /// useful for centering short labels inside Material controls, where the
    /// ascender padding included by GDI+ otherwise makes text look displaced.
    /// </summary>
    public static Vector2 MeasureVisible(
        string text,
        float scale = 1f,
        UiFontWeight weight = UiFontWeight.Regular
    ) {
        return MeasureMetrics(text, RasterContext.Create(scale), weight).VisualBounds.Size;
    }

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
        if (outline > 0f) {
            Color stroke = outlineColor ?? Color.Black;
            DrawCore(text, position + new Vector2(-outline, 0f), justify, context, stroke, weight);
            DrawCore(text, position + new Vector2(outline, 0f), justify, context, stroke, weight);
            DrawCore(text, position + new Vector2(0f, -outline), justify, context, stroke, weight);
            DrawCore(text, position + new Vector2(0f, outline), justify, context, stroke, weight);
        }
        DrawCore(text, position, justify, context, color, weight);
    }

    /// <summary>
    /// Draws with justification applied to the visible glyph ink. Unlike Draw,
    /// a zero justification places the visible top-left at <paramref name="position"/>,
    /// and a 0.5 justification visually centers the text rather than its padded
    /// GDI+ line box.
    /// </summary>
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
        if (outline > 0f) {
            Color stroke = outlineColor ?? Color.Black;
            DrawCoreAligned(text, position + new Vector2(-outline, 0f), origin, context, stroke, weight);
            DrawCoreAligned(text, position + new Vector2(outline, 0f), origin, context, stroke, weight);
            DrawCoreAligned(text, position + new Vector2(0f, -outline), origin, context, stroke, weight);
            DrawCoreAligned(text, position + new Vector2(0f, outline), origin, context, stroke, weight);
        }
        DrawCoreAligned(text, position, origin, context, color, weight);
    }

    public static void Dispose() {
        foreach (Glyph glyph in Glyphs.Values) glyph.Texture?.Dispose();
        Glyphs.Clear();
        foreach (DrawingFont font in Fonts.Values) font.Dispose();
        Fonts.Clear();
        stringFormat?.Dispose();
        stringFormat = null;
        fontFamily?.Dispose();
        fontFamily = null;
        privateFonts?.Dispose();
        privateFonts = null;
        loadedIdentity = "";
    }

    private static void DrawCore(
        string text,
        Vector2 position,
        Vector2 justify,
        RasterContext context,
        Color color,
        UiFontWeight weight
    ) {
        Vector2 origin = MeasureMetrics(text, context, weight).LayoutSize * justify;
        DrawCoreAligned(text, position, origin, context, color, weight);
    }

    private static void DrawCoreAligned(
        string text,
        Vector2 position,
        Vector2 origin,
        RasterContext context,
        Color color,
        UiFontWeight weight
    ) {
        Vector2 cursor = Vector2.Zero;
        foreach (char character in text) {
            if (character == '\n') {
                cursor.X = 0f;
                cursor.Y += context.LineHeight;
                continue;
            }

            Glyph glyph = GetGlyph(character, context.PixelSize, weight);
            if (glyph.Texture is not null) {
                Vector2 at = position + cursor + context.ToLayout(glyph.Offset) - origin;
                // Rasterize at the physical output size, then cancel the active UI transform
                // while drawing. This keeps every glyph texel at 1:1 screen pixels even when
                // Celeste scales its 1920x1080 UI to (for example) 2560x1440.
                at = context.SnapToPixel(at);
                Monocle.Draw.SpriteBatch.Draw(
                    glyph.Texture,
                    at,
                    null,
                    color,
                    0f,
                    Vector2.Zero,
                    context.TextureScale,
                    SpriteEffects.None,
                    0f
                );
            }
            cursor.X += context.ToLayoutX(glyph.Advance);
        }
    }

    private static Glyph GetGlyph(char character, int pixelSize, UiFontWeight weight) {
        Prepare();
        var key = (character, pixelSize, weight);
        if (Glyphs.TryGetValue(key, out Glyph? glyph)) return glyph;
        if (fontFamily is null || stringFormat is null) throw new InvalidOperationException("UI font is not prepared.");

        DrawingFont font = GetFont(pixelSize, weight);
        if (character == '\t')
            return Glyphs[key] = new Glyph(null, pixelSize * 2f,
                Vector2.Zero, Vector2.Zero, Vector2.Zero);

        string value = character.ToString();
        float advance;
        using (DrawingBitmap measure = new(1, 1, PixelFormat.Format32bppArgb))
        using (DrawingGraphics graphics = DrawingGraphics.FromImage(measure)) {
            graphics.PageUnit = DrawingGraphicsUnit.Pixel;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            advance = Math.Max(1f, graphics.MeasureString(value, font, DrawingPointF.Empty, stringFormat).Width);
        }
        advance = MathF.Round(advance);
        if (char.IsWhiteSpace(character))
            return Glyphs[key] = new Glyph(null, advance,
                Vector2.Zero, Vector2.Zero, Vector2.Zero);

        int padding = Math.Max(2, (int)MathF.Ceiling(pixelSize / 10f));
        int width = Math.Max(1, (int)Math.Ceiling(advance) + padding * 2);
        int height = Math.Max(1, (int)Math.Ceiling(BaseLineHeight * pixelSize / BasePixelSize) + padding * 2);
        using DrawingBitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap)) {
            // ClearType cannot produce a useful alpha mask on a transparent surface.
            // Rasterize white onto opaque black, then collapse the RGB subpixel coverage
            // into a grayscale alpha mask in CreateTexture.
            graphics.Clear(System.Drawing.Color.Black);
            graphics.PageUnit = DrawingGraphicsUnit.Pixel;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.TextContrast = 0;
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            graphics.DrawString(value, font, brush, new DrawingPointF(padding, padding), stringFormat);
        }

        TextureData textureData = CreateTexture(bitmap);
        Rectangle ink = textureData.InkBounds;
        Vector2 textureOffset = new(-padding);
        return Glyphs[key] = new Glyph(
            textureData.Texture,
            advance,
            textureOffset,
            textureOffset + new Vector2(ink.X, ink.Y),
            new Vector2(ink.Width, ink.Height)
        );
    }

    private static DrawingFont GetFont(int pixelSize, UiFontWeight weight) {
        var key = (pixelSize, weight);
        if (Fonts.TryGetValue(key, out DrawingFont? font)) return font;
        if (fontFamily is null) throw new InvalidOperationException("UI font is not prepared.");
        DrawingFontStyle style = weight == UiFontWeight.Bold && fontFamily.IsStyleAvailable(DrawingFontStyle.Bold)
            ? DrawingFontStyle.Bold
            : DrawingFontStyle.Regular;
        font = new DrawingFont(fontFamily, pixelSize, style, DrawingGraphicsUnit.Pixel);
        Fonts.Add(key, font);
        return font;
    }

    private static TextureData CreateTexture(DrawingBitmap bitmap) {
        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            Color[] colors = new Color[bitmap.Width * bitmap.Height];
            int inkLeft = bitmap.Width;
            int inkTop = bitmap.Height;
            int inkRight = 0;
            int inkBottom = 0;
            for (int y = 0; y < bitmap.Height; y++) {
                int sourceY = data.Stride < 0 ? bitmap.Height - 1 - y : y;
                int row = sourceY * stride;
                for (int x = 0; x < bitmap.Width; x++) {
                    int source = row + x * 4;
                    int blue = pixels[source];
                    int green = pixels[source + 1];
                    int red = pixels[source + 2];
                    // Preserve ClearType's stronger grid fitting without retaining colored
                    // subpixel fringes. A small coverage boost keeps regular-weight Chinese
                    // strokes from becoming too thin after the RGB mask is collapsed.
                    int luminance = (red * 54 + green * 183 + blue * 19 + 128) >> 8;
                    byte alpha = (byte)Math.Min(255, (luminance * ClearTypeCoverageBoost + 128) >> 8);
                    if (alpha != 0) {
                        inkLeft = Math.Min(inkLeft, x);
                        inkTop = Math.Min(inkTop, y);
                        inkRight = Math.Max(inkRight, x + 1);
                        inkBottom = Math.Max(inkBottom, y + 1);
                    }
                    // Monocle's normal AlphaBlend state expects premultiplied color.
                    // Keeping RGB at 255 when A is zero turns every glyph texture into
                    // a visible white rectangle instead of a transparent background.
                    colors[y * bitmap.Width + x] = new Color(alpha, alpha, alpha, alpha);
                }
            }
            Texture2D texture = new(Engine.Graphics.GraphicsDevice, bitmap.Width, bitmap.Height);
            texture.SetData(colors);
            Rectangle inkBounds = inkRight > inkLeft && inkBottom > inkTop
                ? new Rectangle(inkLeft, inkTop, inkRight - inkLeft, inkBottom - inkTop)
                : Rectangle.Empty;
            return new TextureData(texture, inkBounds);
        } finally {
            bitmap.UnlockBits(data);
        }
    }

    private static TextMetrics MeasureMetrics(string text, RasterContext context, UiFontWeight weight) {
        Prepare();
        if (string.IsNullOrEmpty(text)) return new TextMetrics(Vector2.Zero, FloatRect.Empty);

        float width = 0f;
        float lineWidth = 0f;
        int lineCount = 1;
        float inkLeft = float.MaxValue;
        float inkRight = float.MinValue;
        bool hasInk = false;
        bool lineStartsWithWhitespace = true;
        bool startsWithWhitespace = false;
        bool endsWithWhitespace = false;

        foreach (char character in text) {
            if (character == '\n') {
                width = Math.Max(width, lineWidth);
                lineWidth = 0f;
                lineCount++;
                lineStartsWithWhitespace = true;
                endsWithWhitespace = false;
                continue;
            }

            Glyph glyph = GetGlyph(character, context.PixelSize, weight);
            float advance = context.ToLayoutX(glyph.Advance);
            Vector2 inkOffset = context.ToLayout(glyph.InkOffset);
            Vector2 inkSize = context.ToLayout(glyph.InkSize);
            if (lineStartsWithWhitespace && char.IsWhiteSpace(character)) startsWithWhitespace = true;
            if (!char.IsWhiteSpace(character)) lineStartsWithWhitespace = false;
            endsWithWhitespace = char.IsWhiteSpace(character);
            if (glyph.Texture is not null && inkSize.X > 0f) {
                hasInk = true;
                inkLeft = Math.Min(inkLeft, lineWidth + inkOffset.X);
                inkRight = Math.Max(inkRight, lineWidth + inkOffset.X + inkSize.X);
            }
            lineWidth += advance;
        }
        width = Math.Max(width, lineWidth);

        FloatRect lineInk = GetVisualLineBounds(context, weight);
        float visualLeft = hasInk ? inkLeft : 0f;
        float visualRight = hasInk ? inkRight : width;
        if (startsWithWhitespace) visualLeft = Math.Min(visualLeft, 0f);
        if (endsWithWhitespace) visualRight = Math.Max(visualRight, lineWidth);
        FloatRect visualBounds = new(
            visualLeft,
            lineInk.Y,
            Math.Max(0f, visualRight - visualLeft),
            Math.Max(0f, (lineCount - 1) * context.LineHeight + lineInk.Height)
        );
        return new TextMetrics(new Vector2(width, lineCount * context.LineHeight), visualBounds);
    }

    private static FloatRect GetVisualLineBounds(RasterContext context, UiFontWeight weight) {
        float top = float.MaxValue;
        float bottom = float.MinValue;
        foreach (char character in "Hg国") {
            Glyph glyph = GetGlyph(character, context.PixelSize, weight);
            Vector2 inkOffset = context.ToLayout(glyph.InkOffset);
            Vector2 inkSize = context.ToLayout(glyph.InkSize);
            if (glyph.Texture is null || inkSize.Y <= 0f) continue;
            top = Math.Min(top, inkOffset.Y);
            bottom = Math.Max(bottom, inkOffset.Y + inkSize.Y);
        }
        return bottom > top
            ? new FloatRect(0f, top, 0f, bottom - top)
            : new FloatRect(0f, 0f, 0f, context.LineHeight);
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
            // Fall through to the transform used by the normal high-resolution UI pass.
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
        float LineHeight,
        Vector2 PixelsPerUnit,
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
            Vector2 textureScale = new(1f / pixelsPerUnit.X, 1f / pixelsPerUnit.Y);
            int pixelSize = Math.Max(8,
                (int)MathF.Round(BasePixelSize * Math.Max(0.01f, scale) * pixelsPerUnit.Y));
            float lineHeightPixels = Math.Max(1f,
                MathF.Round(BaseLineHeight * Math.Max(0.01f, scale) * pixelsPerUnit.Y));
            XnaMatrix inverse = screenAligned ? XnaMatrix.Invert(transform) : XnaMatrix.Identity;
            return new RasterContext(
                pixelSize,
                lineHeightPixels / pixelsPerUnit.Y,
                pixelsPerUnit,
                textureScale,
                transform,
                inverse,
                screenAligned
            );
        }

        public float ToLayoutX(float pixels) => pixels / PixelsPerUnit.X;
        public Vector2 ToLayout(Vector2 pixels) => pixels * TextureScale;

        public Vector2 SnapToPixel(Vector2 position) {
            if (!ScreenAligned) return new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
            Vector2 pixel = Vector2.Transform(position, Transform);
            pixel = new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
            return Vector2.Transform(pixel, InverseTransform);
        }
    }

    private sealed record Glyph(
        Texture2D? Texture,
        float Advance,
        Vector2 Offset,
        Vector2 InkOffset,
        Vector2 InkSize
    );

    private sealed record TextureData(Texture2D Texture, Rectangle InkBounds);

    private readonly record struct TextMetrics(Vector2 LayoutSize, FloatRect VisualBounds);
}
