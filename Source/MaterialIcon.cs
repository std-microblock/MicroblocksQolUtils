using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingGraphicsPath = System.Drawing.Drawing2D.GraphicsPath;
using DrawingFillMode = System.Drawing.Drawing2D.FillMode;
using DrawingMatrix = System.Drawing.Drawing2D.Matrix;
using DrawingPointF = System.Drawing.PointF;
using XnaMatrix = Microsoft.Xna.Framework.Matrix;

#pragma warning disable CA1416

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Lazily loads embedded Material Symbols SVGs by their canonical name and
/// rasterizes them for the current graphics device. Add an SVG under
/// Source/MaterialSymbols/Rounded, then reference it as MaterialIcon.Draw("name", ...).
/// </summary>
internal static class MaterialIcon {
    private const int Supersample = 4;
    private const string ResourcePrefix = "Celeste.Mod.MicroblocksQolUtils.MaterialSymbols.Rounded.";
    private const BindingFlags SpriteBatchFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo SpriteBatchBeginCalledField = RequiredSpriteBatchField(
        "beginCalled", "_beginCalled");
    private static readonly FieldInfo SpriteBatchTransformMatrixField = RequiredSpriteBatchField(
        "transformMatrix", "_transformMatrix");
    private static readonly Dictionary<(string Name, int PixelSize), Texture2D?> Textures = new();
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);

    public static void Draw(string name, Vector2 center, float size, Color color, float alpha = 1f) {
        if (size <= 0f || alpha <= 0f) return;
        RasterContext context = RasterContext.Create(size);
        Texture2D? texture = GetTexture(name, context.PixelSize);
        if (texture is null) return;
        Vector2 at = context.SnapToPixel(center - context.LayoutSize / 2f);
        Monocle.Draw.SpriteBatch.Draw(
            texture,
            at,
            null,
            color * alpha,
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

    private static Texture2D? GetTexture(string name, int pixelSize) {
        name = NormalizeName(name);
        if (name.Length == 0) return null;
        var key = (name, pixelSize);
        if (Textures.TryGetValue(key, out Texture2D? texture)) return texture;
        try {
            return Textures[key] = Rasterize(name, pixelSize);
        } catch (Exception exception) {
            if (ReportedFailures.Add(name)) {
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/MaterialIcon",
                    $"Could not load Material Symbol '{name}': {exception.Message}");
            }
            return Textures[key] = null;
        }
    }

    private static Texture2D Rasterize(string name, int rasterSize) {
        Assembly assembly = typeof(MaterialIcon).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName(name))
            ?? throw new FileNotFoundException($"Embedded Material Symbol '{name}' was not found.");
        XDocument document = XDocument.Load(stream);
        XElement root = document.Root ?? throw new InvalidDataException("SVG has no root element.");
        float[] viewBox = ParseNumbers(root.Attribute("viewBox")?.Value ?? "0 0 24 24").ToArray();
        if (viewBox.Length != 4 || viewBox[2] <= 0f || viewBox[3] <= 0f)
            throw new InvalidDataException("SVG viewBox is invalid.");

        using DrawingGraphicsPath combined = new(DrawingFillMode.Winding);
        foreach (XElement path in root.Descendants().Where(element => element.Name.LocalName == "path")) {
            string? data = path.Attribute("d")?.Value;
            if (string.IsNullOrWhiteSpace(data)) continue;
            using DrawingGraphicsPath parsed = SvgPathParser.Parse(data);
            combined.AddPath(parsed, connect: false);
        }

        int sourceSize = rasterSize * Supersample;
        float scale = Math.Min(sourceSize / viewBox[2], sourceSize / viewBox[3]);
        float x = (sourceSize - viewBox[2] * scale) / 2f - viewBox[0] * scale;
        float y = (sourceSize - viewBox[3] * scale) / 2f - viewBox[1] * scale;
        using DrawingMatrix transform = new(scale, 0f, 0f, scale, x, y);
        combined.Transform(transform);

        using DrawingBitmap bitmap = new(sourceSize, sourceSize, PixelFormat.Format32bppArgb);
        using (DrawingGraphics graphics = DrawingGraphics.FromImage(bitmap)) {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            graphics.FillPath(brush, combined);
        }
        return CreateTexture(bitmap, Supersample);
    }

    private static Texture2D CreateTexture(DrawingBitmap bitmap, int sampleScale) {
        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int width = bitmap.Width / sampleScale;
            int height = bitmap.Height / sampleScale;
            Color[] colors = new Color[width * height];
            int sampleCount = sampleScale * sampleScale;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int coverage = 0;
                    for (int sampleY = 0; sampleY < sampleScale; sampleY++) {
                        int bitmapY = y * sampleScale + sampleY;
                        int sourceY = data.Stride < 0 ? bitmap.Height - 1 - bitmapY : bitmapY;
                        int row = sourceY * stride;
                        for (int sampleX = 0; sampleX < sampleScale; sampleX++) {
                            int bitmapX = x * sampleScale + sampleX;
                            coverage += pixels[row + bitmapX * 4 + 3];
                        }
                    }
                    byte alpha = SharpenCoverage((byte)((coverage + sampleCount / 2) / sampleCount));
                    colors[y * width + x] = new Color(alpha, alpha, alpha, alpha);
                }
            }
            Texture2D texture = new(Engine.Graphics.GraphicsDevice, width, height);
            texture.SetData(colors);
            return texture;
        } finally {
            bitmap.UnlockBits(data);
        }
    }

    private static string NormalizeName(string name) => name.Trim().Replace(' ', '_');

    private static string ResourceName(string name) =>
        ResourcePrefix + NormalizeName(name) + ".svg";

    private static byte SharpenCoverage(byte alpha) {
        int value = alpha;
        int smooth = (value * value * (765 - value * 2) + 32512) / 65025;
        return (byte)((value + smooth * 3 + 2) / 4);
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
            return new RasterContext(
                pixelSize,
                new Vector2(pixelSize) * textureScale,
                textureScale,
                transform,
                inverse,
                screenAligned
            );
        }

        public Vector2 SnapToPixel(Vector2 position) {
            if (!ScreenAligned) return new Vector2(MathF.Round(position.X), MathF.Round(position.Y));
            Vector2 pixel = Vector2.Transform(position, Transform);
            pixel = new Vector2(MathF.Round(pixel.X), MathF.Round(pixel.Y));
            return Vector2.Transform(pixel, InverseTransform);
        }
    }

    private static IEnumerable<float> ParseNumbers(string value) {
        SvgNumberReader reader = new(value);
        while (reader.HasNumber) yield return reader.ReadNumber();
    }

    private static class SvgPathParser {
        public static DrawingGraphicsPath Parse(string data) {
            SvgNumberReader reader = new(data);
            DrawingGraphicsPath path = new(DrawingFillMode.Winding);
            DrawingPointF current = new(0f, 0f);
            DrawingPointF figureStart = current;
            DrawingPointF lastQuadraticControl = current;
            char command = '\0';
            char previousCommand = '\0';

            while (!reader.End) {
                if (reader.TryReadCommand(out char explicitCommand)) command = explicitCommand;
                else if (command == '\0') throw new InvalidDataException("SVG path starts without a command.");

                bool relative = char.IsLower(command);
                char operation = char.ToUpperInvariant(command);
                switch (operation) {
                    case 'M': {
                        DrawingPointF point = ReadPoint(reader, relative, current);
                        path.StartFigure();
                        current = point;
                        figureStart = point;
                        previousCommand = command;
                        while (reader.HasNumber) {
                            DrawingPointF next = ReadPoint(reader, relative, current);
                            path.AddLine(current, next);
                            current = next;
                            previousCommand = relative ? 'l' : 'L';
                        }
                        command = relative ? 'l' : 'L';
                        break;
                    }
                    case 'L':
                        while (reader.HasNumber) {
                            DrawingPointF next = ReadPoint(reader, relative, current);
                            path.AddLine(current, next);
                            current = next;
                        }
                        previousCommand = command;
                        break;
                    case 'H':
                        while (reader.HasNumber) {
                            float value = reader.ReadNumber();
                            DrawingPointF next = new(relative ? current.X + value : value, current.Y);
                            path.AddLine(current, next);
                            current = next;
                        }
                        previousCommand = command;
                        break;
                    case 'V':
                        while (reader.HasNumber) {
                            float value = reader.ReadNumber();
                            DrawingPointF next = new(current.X, relative ? current.Y + value : value);
                            path.AddLine(current, next);
                            current = next;
                        }
                        previousCommand = command;
                        break;
                    case 'Q':
                        while (reader.HasNumber) {
                            DrawingPointF control = ReadPoint(reader, relative, current);
                            DrawingPointF end = ReadPoint(reader, relative, current);
                            AddQuadratic(path, current, control, end);
                            current = end;
                            lastQuadraticControl = control;
                            previousCommand = command;
                        }
                        break;
                    case 'T':
                        while (reader.HasNumber) {
                            bool followsQuadratic = char.ToUpperInvariant(previousCommand) is 'Q' or 'T';
                            DrawingPointF control = followsQuadratic
                                ? new DrawingPointF(2f * current.X - lastQuadraticControl.X,
                                    2f * current.Y - lastQuadraticControl.Y)
                                : current;
                            DrawingPointF end = ReadPoint(reader, relative, current);
                            AddQuadratic(path, current, control, end);
                            current = end;
                            lastQuadraticControl = control;
                            previousCommand = command;
                        }
                        break;
                    case 'Z':
                        path.CloseFigure();
                        current = figureStart;
                        previousCommand = command;
                        command = '\0';
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported SVG path command '{command}'.");
                }

                if (operation is not ('Q' or 'T')) lastQuadraticControl = current;
            }
            return path;
        }

        private static DrawingPointF ReadPoint(SvgNumberReader reader, bool relative, DrawingPointF origin) {
            float x = reader.ReadNumber();
            float y = reader.ReadNumber();
            return relative ? new DrawingPointF(origin.X + x, origin.Y + y) : new DrawingPointF(x, y);
        }

        private static void AddQuadratic(
            DrawingGraphicsPath path,
            DrawingPointF start,
            DrawingPointF control,
            DrawingPointF end
        ) {
            DrawingPointF first = new(
                start.X + (control.X - start.X) * 2f / 3f,
                start.Y + (control.Y - start.Y) * 2f / 3f
            );
            DrawingPointF second = new(
                end.X + (control.X - end.X) * 2f / 3f,
                end.Y + (control.Y - end.Y) * 2f / 3f
            );
            path.AddBezier(start, first, second, end);
        }
    }

    private sealed class SvgNumberReader(string data) {
        private int position;

        public bool End {
            get {
                SkipSeparators();
                return position >= data.Length;
            }
        }

        public bool HasNumber {
            get {
                SkipSeparators();
                return position < data.Length && !char.IsLetter(data[position]);
            }
        }

        public bool TryReadCommand(out char command) {
            SkipSeparators();
            if (position < data.Length && char.IsLetter(data[position])) {
                command = data[position++];
                return true;
            }
            command = '\0';
            return false;
        }

        public float ReadNumber() {
            SkipSeparators();
            int start = position;
            if (position < data.Length && data[position] is '+' or '-') position++;
            while (position < data.Length && char.IsDigit(data[position])) position++;
            if (position < data.Length && data[position] == '.') {
                position++;
                while (position < data.Length && char.IsDigit(data[position])) position++;
            }
            if (position < data.Length && data[position] is 'e' or 'E') {
                position++;
                if (position < data.Length && data[position] is '+' or '-') position++;
                while (position < data.Length && char.IsDigit(data[position])) position++;
            }
            if (start == position)
                throw new InvalidDataException($"Expected SVG number at position {position}.");
            return float.Parse(data[start..position], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void SkipSeparators() {
            while (position < data.Length && (char.IsWhiteSpace(data[position]) || data[position] == ',')) position++;
        }
    }
}
