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

#pragma warning disable CA1416

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Lazily loads embedded Material Symbols SVGs by their canonical name and
/// rasterizes them for the current graphics device. Add an SVG under
/// Source/MaterialSymbols/Rounded, then reference it as MaterialIcon.Draw("name", ...).
/// </summary>
internal static class MaterialIcon {
    private const int RasterSize = 128;
    private const string ResourcePrefix = "Celeste.Mod.MicroblocksQolUtils.MaterialSymbols.Rounded.";
    private static readonly Dictionary<string, Texture2D?> Textures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ReportedFailures = new(StringComparer.OrdinalIgnoreCase);

    public static void Draw(string name, Vector2 center, float size, Color color, float alpha = 1f) {
        if (size <= 0f || alpha <= 0f) return;
        Texture2D? texture = GetTexture(name);
        if (texture is null) return;
        Monocle.Draw.SpriteBatch.Draw(
            texture,
            center,
            null,
            color * alpha,
            0f,
            new Vector2(texture.Width / 2f, texture.Height / 2f),
            size / texture.Width,
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

    private static Texture2D? GetTexture(string name) {
        name = NormalizeName(name);
        if (name.Length == 0) return null;
        if (Textures.TryGetValue(name, out Texture2D? texture)) return texture;
        try {
            return Textures[name] = Rasterize(name);
        } catch (Exception exception) {
            if (ReportedFailures.Add(name)) {
                Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/MaterialIcon",
                    $"Could not load Material Symbol '{name}': {exception.Message}");
            }
            return Textures[name] = null;
        }
    }

    private static Texture2D Rasterize(string name) {
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

        float scale = Math.Min(RasterSize / viewBox[2], RasterSize / viewBox[3]);
        float x = (RasterSize - viewBox[2] * scale) / 2f - viewBox[0] * scale;
        float y = (RasterSize - viewBox[3] * scale) / 2f - viewBox[1] * scale;
        using DrawingMatrix transform = new(scale, 0f, 0f, scale, x, y);
        combined.Transform(transform);

        using DrawingBitmap bitmap = new(RasterSize, RasterSize, PixelFormat.Format32bppArgb);
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
        return CreateTexture(bitmap);
    }

    private static Texture2D CreateTexture(DrawingBitmap bitmap) {
        var bounds = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            int stride = Math.Abs(data.Stride);
            byte[] pixels = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            Color[] colors = new Color[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++) {
                int sourceY = data.Stride < 0 ? bitmap.Height - 1 - y : y;
                int row = sourceY * stride;
                for (int x = 0; x < bitmap.Width; x++) {
                    byte alpha = pixels[row + x * 4 + 3];
                    colors[y * bitmap.Width + x] = new Color(alpha, alpha, alpha, alpha);
                }
            }
            Texture2D texture = new(Engine.Graphics.GraphicsDevice, bitmap.Width, bitmap.Height);
            texture.SetData(colors);
            return texture;
        } finally {
            bitmap.UnlockBits(data);
        }
    }

    private static string NormalizeName(string name) => name.Trim().Replace(' ', '_');

    private static string ResourceName(string name) =>
        ResourcePrefix + NormalizeName(name) + ".svg";

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
