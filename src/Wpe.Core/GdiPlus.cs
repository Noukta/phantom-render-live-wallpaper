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
    [LibraryImport("gdiplus.dll")] private static partial int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, nint scan0, out nint bitmap);
    [LibraryImport("gdiplus.dll")] private static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [LibraryImport("gdiplus.dll")] private static partial int GdipGraphicsClear(nint graphics, uint argb);
    [LibraryImport("gdiplus.dll")] private static partial int GdipSetClipRectI(nint graphics, int x, int y, int w, int h, int combine);
    [LibraryImport("gdiplus.dll")] private static partial int GdipResetClip(nint graphics);
    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipSaveImageToFile(nint image, string file, Guid* encoder, nint parameters);

    const int PixelFormat32bppARGB = 0x26200A;
    static readonly Guid JpegEncoder = new("557CF401-1A04-11D3-9A73-0000F81EF32E");

    /// <summary>Draws a file's image scaled to cover a rectangle of a GDI+ graphics, clipped to that rectangle.</summary>
    static bool DrawFileCover(nint g, string file, RECT dest)
    {
        if (GdipCreateBitmapFromFile(file, out nint src) != 0) return false;
        try
        {
            GdipGetImageWidth(src, out uint iw);
            GdipGetImageHeight(src, out uint ih);
            if (iw == 0 || ih == 0) return false;
            double s = Math.Max((double)dest.Width / iw, (double)dest.Height / ih);
            int w = (int)Math.Ceiling(iw * s), h = (int)Math.Ceiling(ih * s);
            GdipSetClipRectI(g, dest.Left, dest.Top, dest.Width, dest.Height, 0);
            int st = GdipDrawImageRectI(g, src, dest.Left + (dest.Width - w) / 2, dest.Top + (dest.Height - h) / 2, w, h);
            GdipResetClip(g);
            return st == 0;
        }
        finally { GdipDisposeImage(src); }
    }

    /// <summary>
    /// Decodes an image file into a new in-memory bitmap of exactly width x height (cover-fit). The file is
    /// closed afterwards, and the result is sized for the screen, so a 4K frame costs only screen-size memory.
    /// </summary>
    public static nint LoadScaled(string file, int width, int height)
    {
        EnsureStarted();
        if (width <= 0 || height <= 0 || GdipCreateBitmapFromScan0(width, height, 0, PixelFormat32bppARGB, 0, out nint dst) != 0) return 0;
        bool ok = false;
        if (GdipGetImageGraphicsContext(dst, out nint g) == 0)
        {
            GdipSetInterpolationMode(g, 7);
            GdipGraphicsClear(g, 0xFF000000);
            ok = DrawFileCover(g, file, new RECT(0, 0, width, height));
            GdipDeleteGraphics(g);
        }
        if (!ok) { GdipDisposeImage(dst); return 0; }
        return dst;
    }

    /// <summary>Paints frames into one picture (black where there is none) and saves it as JPEG.</summary>
    public static bool ComposeJpeg(string outFile, int width, int height, IEnumerable<(string file, RECT dest)> parts)
    {
        EnsureStarted();
        if (width <= 0 || height <= 0 || GdipCreateBitmapFromScan0(width, height, 0, PixelFormat32bppARGB, 0, out nint bmp) != 0) return false;
        try
        {
            if (GdipGetImageGraphicsContext(bmp, out nint g) != 0) return false;
            int drawn = 0;
            GdipSetInterpolationMode(g, 7);
            GdipGraphicsClear(g, 0xFF000000);
            foreach (var (file, dest) in parts) if (DrawFileCover(g, file, dest)) drawn++;
            GdipDeleteGraphics(g);
            if (drawn == 0) return false;
            Guid enc = JpegEncoder;
            string tmp = outFile + ".tmp";
            if (GdipSaveImageToFile(bmp, tmp, &enc, 0) != 0) return false;
            File.Move(tmp, outFile, true);
            return true;
        }
        catch { return false; }
        finally { GdipDisposeImage(bmp); }
    }

    /// <summary>Draws an image at its own size (used for backdrops already scaled to the window).</summary>
    public static void DrawAt(nint hdc, nint image, int width, int height)
    {
        if (image == 0 || GdipCreateFromHDC(hdc, out nint g) != 0) return;
        GdipDrawImageRectI(g, image, 0, 0, width, height);
        GdipDeleteGraphics(g);
    }

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
