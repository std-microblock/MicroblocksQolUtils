using System.Runtime.InteropServices;
using System.Xml.Linq;
using SkiaSharp;

namespace Celeste.Mod.MicroblocksQolUtils;

internal readonly record struct SkiaRasterBounds(float X, float Y, float Width, float Height) {
    public static SkiaRasterBounds Empty => new(0f, 0f, 0f, 0f);
}

internal sealed record SkiaRasterImage(int Width, int Height, byte[] BgraPremultiplied);

internal sealed record SkiaTextRaster(
    SkiaRasterImage? Image,
    float TextureOffsetX,
    float TextureOffsetY,
    float LayoutWidth,
    float LayoutHeight,
    SkiaRasterBounds VisualBounds
);

/// <summary>
/// Pure CPU Skia rasterization shared by the runtime renderer and the parity
/// verifier. The runtime uploads these exact alpha bytes and draws them 1:1.
/// </summary>
internal static class SkiaRasterizer {
    public static SKFont CreateFont(SKTypeface typeface, float pixelSize, bool embolden = false) => new(typeface, pixelSize) {
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.Full,
        BaselineSnap = true,
        Subpixel = false,
        LinearMetrics = false,
        ForceAutoHinting = false,
        Embolden = embolden
    };

    public static SkiaTextRaster RasterizeText(
        string text,
        SKTypeface typeface,
        int pixelSize,
        int lineHeightPixels,
        bool embolden = false,
        SKColor? color = null
    ) {
        string[] lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        using SKFont font = CreateFont(typeface, pixelSize, embolden);
        using SKPaint paint = new() {
            Color = color ?? SKColors.White,
            IsAntialias = true,
            BlendMode = SKBlendMode.SrcOver
        };

        float layoutWidth = 0f;
        foreach (string line in lines) layoutWidth = Math.Max(layoutWidth, font.MeasureText(line, paint));
        float layoutHeight = lines.Length * lineHeightPixels;
        int padding = Math.Max(3, (int)MathF.Ceiling(pixelSize / 5f));
        int bitmapWidth = Math.Max(1, (int)MathF.Ceiling(layoutWidth) + padding * 2 + 1);
        int bitmapHeight = Math.Max(1, (int)MathF.Ceiling(layoutHeight) + padding * 2);

        using SKBitmap bitmap = new(new SKImageInfo(
            bitmapWidth,
            bitmapHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul
        ));
        using (SKCanvas canvas = new(bitmap)) {
            canvas.Clear(SKColors.Transparent);
            SKFontMetrics metrics = font.Metrics;
            float fontHeight = metrics.Descent - metrics.Ascent;
            float baselineInLine = (lineHeightPixels - fontHeight) / 2f - metrics.Ascent;
            for (int index = 0; index < lines.Length; index++) {
                canvas.DrawText(lines[index], padding, padding + index * lineHeightPixels + baselineInLine,
                    SKTextAlign.Left, font, paint);
            }
            canvas.Flush();
        }

        byte[] pixels = ExtractPixels(bitmap);
        byte[] alpha = ExtractAlpha(pixels, bitmapWidth, bitmapHeight, bitmap.RowBytes);
        PixelBounds ink = FindInk(alpha, bitmapWidth, bitmapHeight);
        if (ink.IsEmpty) {
            return new SkiaTextRaster(null, 0f, 0f, layoutWidth, layoutHeight,
                SkiaRasterBounds.Empty);
        }

        byte[] cropped = CropPixels(pixels, bitmap.RowBytes, ink);
        float offsetX = ink.Left - padding;
        float offsetY = ink.Top - padding;
        return new SkiaTextRaster(
            new SkiaRasterImage(ink.Width, ink.Height, cropped),
            offsetX,
            offsetY,
            layoutWidth,
            layoutHeight,
            new SkiaRasterBounds(offsetX, offsetY, ink.Width, ink.Height)
        );
    }

    public static SkiaRasterImage RasterizeSvg(Stream stream, int pixelSize, SKColor? color = null) {
        XDocument document = XDocument.Load(stream);
        XElement root = document.Root ?? throw new InvalidDataException("SVG has no root element.");
        float[] viewBox = ParseNumbers(root.Attribute("viewBox")?.Value ?? "0 0 24 24").ToArray();
        if (viewBox.Length != 4 || viewBox[2] <= 0f || viewBox[3] <= 0f)
            throw new InvalidDataException("SVG viewBox is invalid.");

        List<SKPath> paths = [];
        try {
            foreach (XElement element in root.Descendants().Where(element => element.Name.LocalName == "path")) {
                string? data = element.Attribute("d")?.Value;
                if (string.IsNullOrWhiteSpace(data)) continue;
                paths.Add(SKPath.ParseSvgPathData(data)
                    ?? throw new InvalidDataException("SVG path data is invalid."));
            }

            using SKBitmap bitmap = new(new SKImageInfo(
                pixelSize, pixelSize, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (SKCanvas canvas = new(bitmap))
            using (SKPaint paint = new() {
                Color = color ?? SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                BlendMode = SKBlendMode.SrcOver
            }) {
                canvas.Clear(SKColors.Transparent);
                float scale = Math.Min(pixelSize / viewBox[2], pixelSize / viewBox[3]);
                float x = (pixelSize - viewBox[2] * scale) / 2f - viewBox[0] * scale;
                float y = (pixelSize - viewBox[3] * scale) / 2f - viewBox[1] * scale;
                canvas.Translate(x, y);
                canvas.Scale(scale);
                foreach (SKPath path in paths) canvas.DrawPath(path, paint);
                canvas.Flush();
            }
            return new SkiaRasterImage(pixelSize, pixelSize, ExtractPixelsTight(bitmap));
        } finally {
            foreach (SKPath path in paths) path.Dispose();
        }
    }

    private static byte[] ExtractPixels(SKBitmap bitmap) {
        int stride = bitmap.RowBytes;
        byte[] source = new byte[stride * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), source, 0, source.Length);
        return source;
    }

    private static byte[] ExtractPixelsTight(SKBitmap bitmap) {
        byte[] source = ExtractPixels(bitmap);
        if (bitmap.RowBytes == bitmap.Width * 4) return source;
        byte[] tight = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++)
            Array.Copy(source, y * bitmap.RowBytes, tight, y * bitmap.Width * 4, bitmap.Width * 4);
        return tight;
    }

    private static byte[] ExtractAlpha(byte[] source, int width, int height, int stride) {
        byte[] alpha = new byte[width * height];
        for (int y = 0; y < height; y++) {
            int row = y * stride;
            for (int x = 0; x < width; x++) alpha[y * width + x] = source[row + x * 4 + 3];
        }
        return alpha;
    }

    private static PixelBounds FindInk(byte[] alpha, int width, int height) {
        int left = width;
        int top = height;
        int right = 0;
        int bottom = 0;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                if (alpha[y * width + x] == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }
        return right > left && bottom > top
            ? new PixelBounds(left, top, right - left, bottom - top)
            : PixelBounds.Empty;
    }

    private static byte[] CropPixels(byte[] source, int sourceStride, PixelBounds bounds) {
        byte[] result = new byte[bounds.Width * bounds.Height * 4];
        for (int y = 0; y < bounds.Height; y++) {
            Array.Copy(source, (bounds.Top + y) * sourceStride + bounds.Left * 4,
                result, y * bounds.Width * 4, bounds.Width * 4);
        }
        return result;
    }

    private static IEnumerable<float> ParseNumbers(string value) {
        foreach (string part in value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)) {
            if (!float.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float number))
                throw new InvalidDataException($"Invalid SVG number '{part}'.");
            yield return number;
        }
    }

    private readonly record struct PixelBounds(int Left, int Top, int Width, int Height) {
        public static PixelBounds Empty => new(0, 0, 0, 0);
        public bool IsEmpty => Width <= 0 || Height <= 0;
    }
}
