using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SkiaSharp;

if (args.Length != 1) throw new ArgumentException("Expected the Skia parity output directory.");
string output = Path.GetFullPath(args[0]);
using ParityGame game = new(output);
game.Run();

internal sealed class ParityGame : Game {
    private const int Width = 1120;
    private const int Height = 520;
    private static readonly Color Background = new(24, 22, 34, 255);
    private readonly string output;
    private readonly GraphicsDeviceManager graphics;
    private SpriteBatch? spriteBatch;
    private Texture2D? layerTexture;
    private RenderTarget2D? target;
    private bool rendered;

    public ParityGame(string output) {
        this.output = output;
        graphics = new GraphicsDeviceManager(this) {
            PreferredBackBufferWidth = 1,
            PreferredBackBufferHeight = 1,
            PreferMultiSampling = false,
            SynchronizeWithVerticalRetrace = false,
            GraphicsProfile = GraphicsProfile.HiDef
        };
        IsFixedTimeStep = false;
        IsMouseVisible = false;
    }

    protected override void Initialize() {
        base.Initialize();
        ShowWindow(Window.Handle, 0);
    }

    protected override void LoadContent() {
        spriteBatch = new SpriteBatch(GraphicsDevice);
        target = new RenderTarget2D(GraphicsDevice, Width, Height, false,
            SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        layerTexture = new Texture2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color);
        byte[] source = File.ReadAllBytes(Path.Combine(output, "mod-layer.bgra"));
        if (source.Length != Width * Height * 4) throw new InvalidDataException("Unexpected layer byte count.");
        Color[] colors = new Color[Width * Height];
        for (int index = 0; index < colors.Length; index++) {
            int offset = index * 4;
            colors[index] = new Color(source[offset + 2], source[offset + 1], source[offset], source[offset + 3]);
        }
        layerTexture.SetData(colors);
    }

    protected override void Draw(GameTime gameTime) {
        if (rendered) return;
        rendered = true;
        if (spriteBatch is null || layerTexture is null || target is null)
            throw new InvalidOperationException("GPU resources were not initialized.");

        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Clear(Background);
        spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        spriteBatch.Draw(layerTexture, Vector2.Zero, Color.White);
        spriteBatch.End();
        GraphicsDevice.SetRenderTarget(null);

        Color[] actual = new Color[Width * Height];
        target.GetData(actual);
        byte[] reference = File.ReadAllBytes(Path.Combine(output, "skia-reference.bgra"));
        GpuParityReport report = Compare(actual, reference);
        File.WriteAllText(Path.Combine(output, "gpu-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        SavePng(actual, Path.Combine(output, "gpu-render.png"));
        Console.WriteLine($"GPU foreground similarity: {report.ForegroundSimilarity:P6}");
        Console.WriteLine($"GPU full image similarity: {report.FullImageSimilarity:P6}");
        Console.WriteLine($"GPU exact foreground pixels: {report.ExactForegroundPixelRatio:P6}");
        if (report.ForegroundSimilarity < 0.999) Environment.ExitCode = 1;
        Exit();
        base.Draw(gameTime);
    }

    protected override void UnloadContent() {
        target?.Dispose();
        layerTexture?.Dispose();
        spriteBatch?.Dispose();
        base.UnloadContent();
    }

    private static GpuParityReport Compare(Color[] actual, byte[] reference) {
        if (reference.Length != actual.Length * 4) throw new InvalidDataException("Unexpected reference byte count.");
        long fullError = 0;
        long foregroundError = 0;
        long foregroundPixels = 0;
        long exactForegroundPixels = 0;
        byte[] background = [Background.B, Background.G, Background.R, Background.A];
        for (int index = 0; index < actual.Length; index++) {
            int source = index * 4;
            byte[] gpu = [actual[index].B, actual[index].G, actual[index].R, actual[index].A];
            bool foreground = false;
            bool exact = true;
            for (int channel = 0; channel < 4; channel++) {
                int difference = Math.Abs(gpu[channel] - reference[source + channel]);
                fullError += difference;
                exact &= difference == 0;
                foreground |= gpu[channel] != background[channel]
                    || reference[source + channel] != background[channel];
            }
            if (!foreground) continue;
            foregroundPixels++;
            if (exact) exactForegroundPixels++;
            for (int channel = 0; channel < 4; channel++)
                foregroundError += Math.Abs(gpu[channel] - reference[source + channel]);
        }
        double fullSimilarity = 1d - fullError / (double)(actual.Length * 4L * 255L);
        double foregroundSimilarity = foregroundPixels == 0
            ? 1d
            : 1d - foregroundError / (double)(foregroundPixels * 4L * 255L);
        double exactRatio = foregroundPixels == 0 ? 1d : exactForegroundPixels / (double)foregroundPixels;
        return new GpuParityReport(fullSimilarity, foregroundSimilarity, exactRatio,
            foregroundPixels, fullError, foregroundError);
    }

    private static void SavePng(Color[] colors, string path) {
        using SKBitmap bitmap = new(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte[] pixels = new byte[Width * Height * 4];
        for (int index = 0; index < colors.Length; index++) {
            int offset = index * 4;
            pixels[offset] = colors[index].B;
            pixels[offset + 1] = colors[index].G;
            pixels[offset + 2] = colors[index].R;
            pixels[offset + 3] = colors[index].A;
        }
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private readonly record struct GpuParityReport(
        double FullImageSimilarity,
        double ForegroundSimilarity,
        double ExactForegroundPixelRatio,
        long ForegroundPixels,
        long FullAbsoluteError,
        long ForegroundAbsoluteError
    );
}
