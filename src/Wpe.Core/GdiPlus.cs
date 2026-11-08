using System.Runtime.InteropServices;

namespace Wpe.Core;

/// <summary>Just enough GDI+ (flat API) to draw a still frame, without System.Drawing.</summary>
public static unsafe partial class GdiPlus
{
    [StructLayout(LayoutKind.Sequential)]
    struct StartupInput { public uint Version; public nint Callback; public int SuppressBackgroundThread; public int SuppressExternalCodecs; }

    [LibraryImport("gdiplus.dll")] private static partial int GdiplusStartup(out nint token, StartupInput* input, nint output);
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipCreateBitmapFromFile(string file, out nint bitmap);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCloneImage(nint image, out nint clone);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDisposeImage(nint image);
    [LibraryImport("gdiplus.dll")] private static partial int GdipGetImageWidth(nint image, out uint w);
    [LibraryImport("gdiplus.dll")] private static partial int GdipGetImageHeight(nint image, out uint h);
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateFromHDC(nint hdc, out nint graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipDrawImageRectI(nint graphics, nint image, int x, int y, int w, int h);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetInterpolationMode(nint graphics, int mode);

    static nint s_token;

    static void EnsureStarted()
    {
        if (s_token != 0) return;
        StartupInput si = new() { Version = 1 };
        GdiplusStartup(out s_token, &si, 0);
    }

    /// <summary>Loads an image fully into memory (the file is not kept locked). Returns 0 on failure.</summary>
    public static nint Load(string file)
    {
        EnsureStarted();
        if (GdipCreateBitmapFromFile(file, out nint bmp) != 0) return 0;
        // Cloning decodes the pixels so the file handle can be released.
        nint clone = 0;
        int st = GdipCloneImage(bmp, out clone);
        GdipDisposeImage(bmp);
        return st == 0 ? clone : 0;
    }

    public static void Dispose(nint image) { if (image != 0) GdipDisposeImage(image); }

    /// <summary>Draws the image scaled to cover the rectangle (cropping the overflow), centered.</summary>
    public static void DrawCover(nint hdc, nint image, int width, int height)
    {
        if (image == 0) return;
        GdipGetImageWidth(image, out uint iw);
        GdipGetImageHeight(image, out uint ih);
        if (iw == 0 || ih == 0 || GdipCreateFromHDC(hdc, out nint g) != 0) return;
        double s = Math.Max((double)width / iw, (double)height / ih);
        int w = (int)Math.Ceiling(iw * s), h = (int)Math.Ceiling(ih * s);
        GdipSetInterpolationMode(g, 7 /* HighQualityBicubic */);
        GdipDrawImageRectI(g, image, (width - w) / 2, (height - h) / 2, w, h);
        GdipDeleteGraphics(g);
    }
}
