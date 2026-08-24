using System.Runtime.InteropServices;
using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal readonly record struct PortableRasterBounds(float X, float Y, float Width, float Height) {
    public static PortableRasterBounds Empty => new(0f, 0f, 0f, 0f);
}

internal sealed record PortableRasterImage(int Width, int Height, byte[] BgraPremultiplied);

internal sealed record PortableTextRaster(
    PortableRasterImage? Image,
    float TextureOffsetX,
    float TextureOffsetY,
    float LayoutWidth,
    float LayoutHeight,
    PortableRasterBounds VisualBounds
);

/// <summary>
/// Managed bridge to the small cross-platform Rust rasterizer in the existing
/// native mod library. No Skia runtime is loaded or packaged by the mod.
/// </summary>
internal static class PortableRasterizer {
    private const string LibraryName = "microblocks_qol_native";

    public static PortableTextRaster RasterizeText(
        string text,
        string fontFamily,
        string fontFile,
        bool bold,
        int pixelSize,
        int lineHeight,
        byte red,
        byte green,
        byte blue
    ) {
        NativeCaptureBridge.Initialize(null);
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new {
            text,
            font_family = fontFamily,
            font_file = fontFile,
            bold,
            pixel_size = pixelSize,
            line_height = lineHeight,
            red,
            green,
            blue
        });
        ThrowIfFailed(RasterText(request, (nuint)request.Length, out NativeRasterResult result), "text");
        byte[] pixels = TakeBytes(result);
        PortableRasterImage? image = result.Width == 0 || result.Height == 0
            ? null
            : new PortableRasterImage((int)result.Width, (int)result.Height, pixels);
        return new PortableTextRaster(
            image,
            result.TextureOffsetX,
            result.TextureOffsetY,
            result.LayoutWidth,
            result.LayoutHeight,
            new PortableRasterBounds(result.VisualX, result.VisualY,
                result.VisualWidth, result.VisualHeight)
        );
    }

    public static PortableRasterImage RasterizeSvg(
        Stream stream,
        int pixelSize,
        byte red,
        byte green,
        byte blue
    ) {
        NativeCaptureBridge.Initialize(null);
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        byte[] svg = memory.ToArray();
        ThrowIfFailed(RasterSvg(svg, (nuint)svg.Length, (uint)pixelSize,
            red, green, blue, out NativeRasterResult result), "SVG");
        byte[] pixels = TakeBytes(result);
        return new PortableRasterImage((int)result.Width, (int)result.Height, pixels);
    }

    public static IReadOnlyList<string> FontFamilies() {
        NativeCaptureBridge.Initialize(null);
        ThrowIfFailed(RasterFontFamilies(out NativeRasterResult result), "font family enumeration");
        byte[] json = TakeBytes(result);
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    private static byte[] TakeBytes(NativeRasterResult result) {
        if (result.Pixels == IntPtr.Zero || result.PixelsLength == 0) return [];
        if (result.PixelsLength > int.MaxValue) {
            RasterFree(result.Pixels, result.PixelsLength);
            throw new InvalidDataException("Native raster output is too large.");
        }
        byte[] bytes = new byte[(int)result.PixelsLength];
        try {
            Marshal.Copy(result.Pixels, bytes, 0, bytes.Length);
        } finally {
            RasterFree(result.Pixels, result.PixelsLength);
        }
        return bytes;
    }

    private static void ThrowIfFailed(int status, string operation) {
        if (status == 0) return;
        throw new InvalidOperationException($"Portable raster {operation} failed ({status}): "
            + NativeCaptureBridge.LastError());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRasterResult {
        public IntPtr Pixels;
        public nuint PixelsLength;
        public uint Width;
        public uint Height;
        public float TextureOffsetX;
        public float TextureOffsetY;
        public float LayoutWidth;
        public float LayoutHeight;
        public float VisualX;
        public float VisualY;
        public float VisualWidth;
        public float VisualHeight;
    }

    [DllImport(LibraryName, EntryPoint = "mqol_raster_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RasterText(byte[] request, nuint requestLength, out NativeRasterResult result);

    [DllImport(LibraryName, EntryPoint = "mqol_raster_svg", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RasterSvg(byte[] svg, nuint svgLength, uint pixelSize,
        byte red, byte green, byte blue, out NativeRasterResult result);

    [DllImport(LibraryName, EntryPoint = "mqol_raster_font_families", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RasterFontFamilies(out NativeRasterResult result);

    [DllImport(LibraryName, EntryPoint = "mqol_raster_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void RasterFree(IntPtr pixels, nuint pixelsLength);
}
