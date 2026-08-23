using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Celeste.Mod.MicroblocksQolUtils;
using SkiaSharp;

const int Width = 1120;
const int Height = 520;
SKColor background = new(24, 22, 34, 255);
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(root, ".work", "skia-parity");
Directory.CreateDirectory(output);

using SKTypeface regular = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Normal)
    ?? throw new InvalidOperationException("Microsoft YaHei UI regular was not found.");
using SKTypeface bold = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyle.Bold)
    ?? throw new InvalidOperationException("Microsoft YaHei UI bold was not found.");
using SKBitmap reference = NewCanvas();
using SKBitmap simulated = NewCanvas();

TextSpec[] texts = [
    new("Microblock 的 QOL 工具", 76, 44, 31, 40, true, new SKColor(242, 239, 249)),
    new("MATERIAL YOU  ·  CELESTE UTILITIES", 78, 91, 9, 14, false, new SKColor(160, 132, 210)),
    new("小地图", 310, 153, 21, 27, true, new SKColor(239, 235, 245)),
    new("ROOM OVERVIEW", 311, 185, 9, 14, false, new SKColor(139, 117, 181)),
    new("启用小地图", 305, 245, 15, 19, true, new SKColor(226, 222, 233)),
    new("地图尺寸", 305, 311, 15, 19, true, new SKColor(226, 222, 233)),
    new("键盘快捷键", 305, 377, 15, 19, true, new SKColor(226, 222, 233)),
    new("使用 Everest 设置", 510, 422, 15, 19, true, new SKColor(249, 246, 253)),
    new("224 px", 805, 311, 13, 17, true, new SKColor(226, 222, 233)),
];

IconSpec[] icons = [
    new("deployed_code", 38, 43, 31, new SKColor(169, 137, 226)),
    new("map", 273, 153, 27, new SKColor(169, 137, 226)),
    new("map", 270, 244, 18, new SKColor(169, 137, 226)),
    new("crop", 270, 310, 18, new SKColor(169, 137, 226)),
    new("keyboard", 270, 376, 18, new SKColor(169, 137, 226)),
];

using (SKCanvas canvas = new(reference)) {
    foreach (TextSpec spec in texts) DrawTextReference(canvas, spec);
    foreach (IconSpec spec in icons) DrawIconReference(canvas, spec);
    canvas.Flush();
}

using (SKCanvas canvas = new(simulated)) {
    foreach (TextSpec spec in texts) DrawTextSimulated(canvas, spec);
    foreach (IconSpec spec in icons) DrawIconSimulated(canvas, spec);
    canvas.Flush();
}

string referencePath = Path.Combine(output, "skia-reference.png");
string simulatedPath = Path.Combine(output, "mod-simulated.png");
SavePng(reference, referencePath);
SavePng(simulated, simulatedPath);
ParityReport report = Compare(reference, simulated, background);
string reportPath = Path.Combine(output, "report.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Skia reference: {referencePath}");
Console.WriteLine($"Mod simulation: {simulatedPath}");
Console.WriteLine($"Foreground similarity: {report.ForegroundSimilarity:P6}");
Console.WriteLine($"Full image similarity: {report.FullImageSimilarity:P6}");
Console.WriteLine($"Exact foreground pixels: {report.ExactForegroundPixelRatio:P6}");
if (report.ForegroundSimilarity < 0.999) Environment.ExitCode = 1;

SKBitmap NewCanvas() {
    SKBitmap bitmap = new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    using SKCanvas canvas = new(bitmap);
    canvas.Clear(background);
    return bitmap;
}

void DrawTextReference(SKCanvas canvas, TextSpec spec) {
    using SKFont font = SkiaRasterizer.CreateFont(spec.Bold ? bold : regular, spec.PixelSize);
    using SKPaint paint = new() { Color = spec.Color, IsAntialias = true, BlendMode = SKBlendMode.SrcOver };
    SKFontMetrics metrics = font.Metrics;
    float baseline = spec.Y + (spec.LineHeight - (metrics.Descent - metrics.Ascent)) / 2f - metrics.Ascent;
    canvas.DrawText(spec.Text, spec.X, baseline, SKTextAlign.Left, font, paint);
}

void DrawTextSimulated(SKCanvas canvas, TextSpec spec) {
    SkiaTextRaster raster = SkiaRasterizer.RasterizeText(spec.Text,
        spec.Bold ? bold : regular, spec.PixelSize, spec.LineHeight, color: spec.Color);
    if (raster.Image is null) return;
    using SKBitmap mask = RasterBitmap(raster.Image);
    canvas.DrawBitmap(mask, spec.X + raster.TextureOffsetX, spec.Y + raster.TextureOffsetY,
        new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
}

void DrawIconReference(SKCanvas canvas, IconSpec spec) {
    string path = Path.Combine(root, "Source", "MaterialSymbols", "Rounded", spec.Name + ".svg");
    XDocument document = XDocument.Load(path);
    XElement svg = document.Root ?? throw new InvalidDataException("SVG has no root.");
    float[] viewBox = svg.Attribute("viewBox")!.Value
        .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
        .Select(value => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
        .ToArray();
    using SKPaint paint = new() { Color = spec.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
    float scale = Math.Min(spec.PixelSize / viewBox[2], spec.PixelSize / viewBox[3]);
    float x = spec.X + (spec.PixelSize - viewBox[2] * scale) / 2f - viewBox[0] * scale;
    float y = spec.Y + (spec.PixelSize - viewBox[3] * scale) / 2f - viewBox[1] * scale;
    canvas.Save();
    canvas.Translate(x, y);
    canvas.Scale(scale);
    foreach (XElement element in svg.Descendants().Where(element => element.Name.LocalName == "path")) {
        using SKPath parsed = SKPath.ParseSvgPathData(element.Attribute("d")!.Value)
            ?? throw new InvalidDataException("Invalid SVG path.");
        canvas.DrawPath(parsed, paint);
    }
    canvas.Restore();
}

void DrawIconSimulated(SKCanvas canvas, IconSpec spec) {
    string path = Path.Combine(root, "Source", "MaterialSymbols", "Rounded", spec.Name + ".svg");
    using Stream stream = File.OpenRead(path);
    SkiaRasterImage image = SkiaRasterizer.RasterizeSvg(stream, spec.PixelSize, spec.Color);
    using SKBitmap mask = RasterBitmap(image);
    canvas.DrawBitmap(mask, spec.X, spec.Y,
        new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
}

SKBitmap RasterBitmap(SkiaRasterImage image) {
    SKBitmap bitmap = new(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    if (bitmap.RowBytes == image.Width * 4) {
        Marshal.Copy(image.BgraPremultiplied, 0, bitmap.GetPixels(), image.BgraPremultiplied.Length);
    } else {
        byte[] pixels = new byte[bitmap.RowBytes * bitmap.Height];
        for (int y = 0; y < image.Height; y++) {
            Array.Copy(image.BgraPremultiplied, y * image.Width * 4,
                pixels, y * bitmap.RowBytes, image.Width * 4);
        }
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    }
    return bitmap;
}

void SavePng(SKBitmap bitmap, string path) {
    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    using FileStream stream = File.Create(path);
    data.SaveTo(stream);
}

ParityReport Compare(SKBitmap first, SKBitmap second, SKColor bg) {
    byte[] a = Bytes(first);
    byte[] b = Bytes(second);
    long fullError = 0;
    long foregroundError = 0;
    long foregroundChannels = 0;
    long foregroundPixels = 0;
    long exactForegroundPixels = 0;
    byte[] bgra = [bg.Blue, bg.Green, bg.Red, bg.Alpha];
    for (int y = 0; y < Height; y++) {
        for (int x = 0; x < Width; x++) {
            int index = y * first.RowBytes + x * 4;
            bool foreground = false;
            bool exact = true;
            for (int channel = 0; channel < 4; channel++) {
                int difference = Math.Abs(a[index + channel] - b[index + channel]);
                fullError += difference;
                exact &= difference == 0;
                foreground |= a[index + channel] != bgra[channel] || b[index + channel] != bgra[channel];
            }
            if (!foreground) continue;
            foregroundPixels++;
            if (exact) exactForegroundPixels++;
            foregroundChannels += 4;
            for (int channel = 0; channel < 4; channel++)
                foregroundError += Math.Abs(a[index + channel] - b[index + channel]);
        }
    }
    double fullSimilarity = 1d - fullError / (double)(Width * Height * 4L * 255L);
    double foregroundSimilarity = foregroundChannels == 0
        ? 1d
        : 1d - foregroundError / (double)(foregroundChannels * 255L);
    double exactRatio = foregroundPixels == 0 ? 1d : exactForegroundPixels / (double)foregroundPixels;
    return new ParityReport(fullSimilarity, foregroundSimilarity, exactRatio,
        foregroundPixels, fullError, foregroundError);
}

byte[] Bytes(SKBitmap bitmap) {
    byte[] bytes = new byte[bitmap.RowBytes * bitmap.Height];
    Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
    return bytes;
}

readonly record struct TextSpec(
    string Text, int X, int Y, int PixelSize, int LineHeight, bool Bold, SKColor Color);
readonly record struct IconSpec(string Name, int X, int Y, int PixelSize, SKColor Color);
readonly record struct ParityReport(
    double FullImageSimilarity,
    double ForegroundSimilarity,
    double ExactForegroundPixelRatio,
    long ForegroundPixels,
    long FullAbsoluteError,
    long ForegroundAbsoluteError
);
