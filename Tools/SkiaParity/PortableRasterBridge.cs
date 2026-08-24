using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Celeste.Mod.MicroblocksQolUtils;

internal sealed record PortableReferenceImage(int Width, int Height, byte[] BgraPremultiplied);
internal sealed record PortableReferenceText(
    PortableReferenceImage? Image,
    float TextureOffsetX,
    float TextureOffsetY,
    float LayoutWidth,
    float LayoutHeight
);

internal static class PortableRasterBridge {
    private const string LibraryName = "microblocks_qol_native";
    private static IntPtr handle;

    public static void Initialize(string root) {
        if (handle != IntPtr.Zero) return;
        string fileName = OperatingSystem.IsWindows()
            ? "microblocks_qol_native.dll"
            : OperatingSystem.IsMacOS()
                ? "libmicroblocks_qol_native.dylib"
                : "libmicroblocks_qol_native.so";
        string path = Path.Combine(root, "target", "release", fileName);
        handle = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(PortableRasterBridge).Assembly,
            (name, _, _) => string.Equals(name, LibraryName, StringComparison.Ordinal) ? handle : IntPtr.Zero);
    }

    public static PortableReferenceText RasterizeText(
        string text,
        string family,
        bool bold,
        int pixelSize,
        int lineHeight,
        byte red,
        byte green,
        byte blue
    ) {
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new {
            text,
            font_family = family,
            font_file = "",
            bold,
            pixel_size = pixelSize,
            line_height = lineHeight,
            red,
            green,
            blue
        });
        Check(RasterText(request, (nuint)request.Length, out NativeRasterResult result));
        byte[] bytes = TakeBytes(result);
        return new PortableReferenceText(
            result.Width == 0 || result.Height == 0
                ? null
                : new PortableReferenceImage((int)result.Width, (int)result.Height, bytes),
            result.TextureOffsetX,
            result.TextureOffsetY,
            result.LayoutWidth,
            result.LayoutHeight
        );
    }

    public static PortableReferenceImage RasterizeSvg(byte[] svg, int pixelSize,
        byte red, byte green, byte blue) {
        Check(RasterSvg(svg, (nuint)svg.Length, (uint)pixelSize, red, green, blue,
            out NativeRasterResult result));
        return new PortableReferenceImage((int)result.Width, (int)result.Height, TakeBytes(result));
    }

    private static byte[] TakeBytes(NativeRasterResult result) {
        if (result.Pixels == IntPtr.Zero || result.PixelsLength == 0) return [];
        byte[] bytes = new byte[checked((int)result.PixelsLength)];
        try {
            Marshal.Copy(result.Pixels, bytes, 0, bytes.Length);
        } finally {
            RasterFree(result.Pixels, result.PixelsLength);
        }
        return bytes;
    }

    private static void Check(int status) {
        if (status == 0) return;
        throw new InvalidOperationException($"Portable native raster failed with status {status}.");
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
    private static extern int RasterText(byte[] request, nuint length, out NativeRasterResult result);

    [DllImport(LibraryName, EntryPoint = "mqol_raster_svg", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RasterSvg(byte[] svg, nuint length, uint pixelSize,
        byte red, byte green, byte blue, out NativeRasterResult result);

    [DllImport(LibraryName, EntryPoint = "mqol_raster_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void RasterFree(IntPtr pixels, nuint length);
}
