<#
.SYNOPSIS
    Generates the GUI branding assets from the Kraus-Ahma logo files in claudeinput\Logo.

.DESCRIPTION
    Output in src\Keepsake.Gui\Assets\:
      logo-color.png    original colours, cropped (light backgrounds: About dialog, login)
      logo-on-dark.png  white lettering, light grey tagline, coloured stripes (dark sidebar)
      mark.png          the three stripes only (square)
      keepsake.ico      application icon from the stripes (16–256 px)
    Brand colours (claudeinput\Logo\Farbwerte.txt): red #B11E22, pink #F38594, light pink #F7ACBC.
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

    /// <summary>Removes everything except the stripes.</summary>
    public static void StripesOnly(Bitmap b)
    {
        BitmapData d; var px = Bytes(b, out d);
        for (int i = 0; i < px.Length; i += 4)
            if (!Saturated(px[i + 2], px[i + 1], px[i])) px[i + 3] = 0;
        Store(b, d, px);
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

    /// <summary>ICO with PNG-compressed images (supported since Windows Vista / Server 2008).</summary>
    public static void WriteIco(Bitmap square, string path, int[] sizes)
    {
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var s = Resize(square, sizes[i], sizes[i]))
            using (var ms = new MemoryStream()) { s.Save(ms, ImageFormat.Png); images[i] = ms.ToArray(); }
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

    # Stripes only: mark and icon.
    $stripes = $original.Clone()
    [Brand]::StripesOnly($stripes)
    $markBounds = [Brand]::Bounds($stripes, $true)
    $mark = [Brand]::Crop($stripes, $markBounds, [int]($markBounds.Height * 0.08), $true)
    $markPng = [Brand]::Resize($mark, 256, 256); $markPng.Save((Join-Path $out 'mark.png'), [System.Drawing.Imaging.ImageFormat]::Png); $markPng.Dispose()
    [Brand]::WriteIco($mark, (Join-Path $out 'keepsake.ico'), [int[]](16, 20, 24, 32, 40, 48, 64, 128, 256))
    $mark.Dispose(); $stripes.Dispose()
}
finally {
    $original.Dispose()
}
Get-ChildItem $out | ForEach-Object { '{0,-18} {1,8:N0} bytes' -f $_.Name, $_.Length }
