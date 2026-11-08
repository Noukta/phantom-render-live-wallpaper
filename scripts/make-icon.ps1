# Turns the app artwork (a glowing ghost on a black background) into assets\icon.png (transparent, 512 px)
# and assets\phantom.ico (16-256 px, PNG-compressed frames).
# Usage: scripts\make-icon.ps1 -Source <image>
param([Parameter(Mandatory)][string]$Source)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Knockout {
    // Black background -> transparency. Alpha follows brightness near black, and colors are un-premultiplied
    // so the soft glow keeps its hue instead of turning into a dark fringe.
    public static Bitmap Apply(Bitmap src, int full) {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var r = new Rectangle(0, 0, src.Width, src.Height);
        var s = src.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var d = dst.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        int n = s.Stride * src.Height; var px = new byte[n]; Marshal.Copy(s.Scan0, px, 0, n);
        for (int i = 0; i < n; i += 4) {
            int b = px[i], g = px[i + 1], rr = px[i + 2];
            int m = Math.Max(rr, Math.Max(g, b));
            int a = m >= full ? 255 : (m <= 6 ? 0 : (m - 6) * 255 / (full - 6));
            if (a > 0 && a < 255) {
                px[i] = (byte)Math.Min(255, b * 255 / a); px[i + 1] = (byte)Math.Min(255, g * 255 / a); px[i + 2] = (byte)Math.Min(255, rr * 255 / a);
            }
            px[i + 3] = (byte)a;
        }
        Marshal.Copy(px, 0, d.Scan0, n); src.UnlockBits(s); dst.UnlockBits(d); return dst;
    }
    public static Rectangle Bounds(Bitmap bmp) {
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        var r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var s = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int n = s.Stride * bmp.Height; var px = new byte[n]; Marshal.Copy(s.Scan0, px, 0, n); bmp.UnlockBits(s);
        for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++)
            if (px[y * s.Stride + x * 4 + 3] > 16) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }
}
'@

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$src = New-Object System.Drawing.Bitmap (Resolve-Path $Source).Path
$cut = [Knockout]::Apply($src, 70)
$b = [Knockout]::Bounds($cut)
# Square canvas around the ghost with a small margin.
$side = [int]([Math]::Max($b.Width, $b.Height) * 1.04)
$square = New-Object System.Drawing.Bitmap $side, $side, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($square)
$g.DrawImage($cut, [int](($side - $b.Width) / 2), [int](($side - $b.Height) / 2), $b, [System.Drawing.GraphicsUnit]::Pixel)
$g.Dispose()

function Resize($bmp, $size) {
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gg = [System.Drawing.Graphics]::FromImage($out)
    $gg.InterpolationMode = 'HighQualityBicubic'; $gg.PixelOffsetMode = 'HighQuality'; $gg.SmoothingMode = 'HighQuality'; $gg.CompositingQuality = 'HighQuality'
    $gg.DrawImage($bmp, 0, 0, $size, $size); $gg.Dispose(); $out
}

(Resize $square 512).Save((Join-Path $assets 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# ICO with PNG frames (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { $ms = New-Object IO.MemoryStream; (Resize $square $s).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() }
$ico = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
[IO.File]::WriteAllBytes((Join-Path $assets 'phantom.ico'), $ico.ToArray())
"Wrote assets\icon.png and assets\phantom.ico ($($sizes -join ', ') px)"
