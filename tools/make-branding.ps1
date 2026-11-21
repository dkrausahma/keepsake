<#
.SYNOPSIS
    Generates the GUI branding assets: the company logo from claudeinput\Logo, the Keepsake icon drawn here.

.DESCRIPTION
    Output in src\Keepsake.Gui\Assets\:
      logo-color.png    Kraus-Ahma logo, original colours, cropped (light backgrounds: About dialog, login)
      logo-on-dark.png  white lettering, light grey tagline, coloured stripes (dark sidebar)
      mark.png          Keepsake product mark: the locked keepsake box (256 px, next to the product name)
      keepsake.ico      application icon, the same box (16–256 px; 16–24 px use simplified shapes)
    Brand colours (claudeinput\Logo\Farbwerte.txt): red #B11E22, pink #F38594, light pink #F7ACBC;
    charcoal #282C2F as in the GUI sidebar. The icon is design direction A ("Keepsake box").
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'claudeinput\Logo\Kraus-Ahma logo__color.png'
$out = Join-Path $root 'src\Keepsake.Gui\Assets'
New-Item -ItemType Directory -Force $out | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class Brand
{
    static byte[] Bytes(Bitmap b, out BitmapData d)
    {
        d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var bytes = new byte[d.Stride * b.Height];
        Marshal.Copy(d.Scan0, bytes, 0, bytes.Length);
        return bytes;
    }

    static void Store(Bitmap b, BitmapData d, byte[] bytes)
    {
        Marshal.Copy(bytes, 0, d.Scan0, bytes.Length);
        b.UnlockBits(d);
    }

    static bool Saturated(byte r, byte g, byte bl) { return r - Math.Max(g, bl) > 40; }

    /// <summary>Bounding box of visible pixels (optionally only the coloured stripes).</summary>
    public static Rectangle Bounds(Bitmap b, bool stripesOnly)
    {
        BitmapData d; var px = Bytes(b, out d);
        int minX = b.Width, minY = b.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int i = y * d.Stride + x * 4;
                if (px[i + 3] < 24) continue;
                if (stripesOnly && !Saturated(px[i + 2], px[i + 1], px[i])) continue;
                if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y;
            }
        b.UnlockBits(d);
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>Lettering white, tagline light grey, stripes unchanged.</summary>
    public static void ForDarkBackground(Bitmap b)
    {
        BitmapData d; var px = Bytes(b, out d);
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] == 0 || Saturated(px[i + 2], px[i + 1], px[i])) continue;
            int lum = (px[i] + px[i + 1] + px[i + 2]) / 3;
            byte v = (byte)(lum < 80 ? 255 : 196); // charcoal lettering → white, grey tagline → light grey
            px[i] = v; px[i + 1] = v; px[i + 2] = v;
        }
        Store(b, d, px);
    }

    static readonly Color Charcoal = ColorTranslator.FromHtml("#282C2F");
    static readonly Color Red = ColorTranslator.FromHtml("#B11E22");
    static readonly Color RedDark = ColorTranslator.FromHtml("#8A161A");
    static readonly Color Pink = ColorTranslator.FromHtml("#F38594");
    static readonly Color LightPink = ColorTranslator.FromHtml("#F7ACBC");

    /// <summary>
    /// The Keepsake icon at one size, drawn on a 256-unit grid: charcoal tile, red box, pink lid, light pink clasp
    /// with a keyhole. Up to 24 px the shapes are chunkier and the keyhole and lid shadow are left out, and every
    /// edge is snapped to whole pixels so the small sizes stay sharp.
    /// </summary>
    public static Bitmap KeepsakeIcon(int size)
    {
        bool small = size <= 24;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float k = size / 256f;
            Func<float, float, float, float, float, Color, int> box = (x, y, w, h, r, c) =>
            {
                float x0 = x * k, y0 = y * k, x1 = (x + w) * k, y1 = (y + h) * k;
                if (size <= 48) { x0 = (float)Math.Round(x0); y0 = (float)Math.Round(y0); x1 = (float)Math.Round(x1); y1 = (float)Math.Round(y1); }
                using (var path = Rounded(x0, y0, x1 - x0, y1 - y0, r * k))
                using (var brush = new SolidBrush(c))
                    g.FillPath(brush, path);
                return 0;
            };
            if (small)
            {
                box(0, 0, 256, 256, 48, Charcoal);
                box(40, 112, 176, 112, 16, Red);
                box(24, 56, 208, 64, 16, Pink);
                box(100, 96, 56, 64, 12, LightPink);
            }
            else
            {
                box(0, 0, 256, 256, 56, Charcoal);
                box(52, 112, 152, 94, 14, Red);
                box(52, 118, 152, 8, 0, RedDark);
                box(42, 74, 172, 46, 12, Pink);
                box(110, 100, 36, 42, 7, LightPink);
                using (var brush = new SolidBrush(Charcoal))
                    g.FillEllipse(brush, 122 * k, 110 * k, 12 * k, 12 * k);
                box(125, 118, 6, 14, 2, Charcoal);
            }
        }
        return bmp;
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        r = Math.Min(r, Math.Min(w, h) / 2);
        if (r < 0.5f) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
        float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>ICO with one PNG per size, each drawn at that size (PNG entries: supported since Vista / Server 2008).</summary>
    public static void WriteKeepsakeIco(string path, int[] sizes)
    {
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var s = KeepsakeIcon(sizes[i]))
            using (var ms = new MemoryStream()) { s.Save(ms, ImageFormat.Png); images[i] = ms.ToArray(); }
        WriteIcoImages(path, sizes, images);
    }

    public static Bitmap Crop(Bitmap b, Rectangle r, int pad, bool square)
    {
        int w = r.Width + 2 * pad, h = r.Height + 2 * pad;
        if (square) { w = h = Math.Max(w, h); }
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(b, new Rectangle((w - r.Width) / 2, (h - r.Height) / 2, r.Width, r.Height), r, GraphicsUnit.Pixel);
        }
        return dst;
    }

    public static Bitmap Resize(Bitmap b, int width, int height)
    {
        var dst = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(b, 0, 0, width, height);
        }
        return dst;
    }

    static void WriteIcoImages(string path, int[] sizes, byte[][] images)
    {
        using (var f = new BinaryWriter(File.Create(path)))
        {
            f.Write((short)0); f.Write((short)1); f.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); f.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                f.Write((byte)0); f.Write((byte)0); f.Write((short)1); f.Write((short)32);
                f.Write(images[i].Length); f.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) f.Write(img);
        }
    }
}
"@

$original = [System.Drawing.Bitmap]::FromFile($source)
try {
    $all = [Brand]::Bounds($original, $false)

    # Colour logo for light backgrounds: 160 px high (sharp at 200 % display scaling for ~80 px).
    $color = [Brand]::Crop($original, $all, 20, $false)
    $h = 160; $w = [int]($color.Width * $h / $color.Height)
    $small = [Brand]::Resize($color, $w, $h); $small.Save((Join-Path $out 'logo-color.png'), [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose()

    # Variant for the dark sidebar.
    [Brand]::ForDarkBackground($color)
    $small = [Brand]::Resize($color, $w, $h); $small.Save((Join-Path $out 'logo-on-dark.png'), [System.Drawing.Imaging.ImageFormat]::Png); $small.Dispose()
    $color.Dispose()

    # Keepsake product mark and application icon: the locked keepsake box.
    $mark = [Brand]::KeepsakeIcon(256); $mark.Save((Join-Path $out 'mark.png'), [System.Drawing.Imaging.ImageFormat]::Png); $mark.Dispose()
    [Brand]::WriteKeepsakeIco((Join-Path $out 'keepsake.ico'), [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256))
}
finally {
    $original.Dispose()
}
Get-ChildItem $out | ForEach-Object { '{0,-18} {1,8:N0} bytes' -f $_.Name, $_.Length }
