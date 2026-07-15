// One-shot generator for RealmChat's app.ico: the same speech-bubble mark the
// app draws at runtime (AppIcons.cs geometry), rendered at every shell size
// and packed as PNG-framed ICO (Vista+). Original art, no external assets.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IcoGen
{
    private static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Draw(s))
            {
                // Classic 32bpp DIB frames for shell/GDI sizes (GDI+ can't
                // rasterize PNG frames at runtime); PNG for the big ones.
                frames.Add(s <= 64 ? ToDib(bmp) : ToPng(bmp));
            }
        }

        using (var f = File.Create(args[0]))
        using (var w = new BinaryWriter(f))
        {
            w.Write((short)0);            // reserved
            w.Write((short)1);            // type: icon
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));   // width (0 = 256)
                w.Write((byte)(s >= 256 ? 0 : s));   // height
                w.Write((byte)0);                     // palette
                w.Write((byte)0);                     // reserved
                w.Write((short)1);                    // planes
                w.Write((short)32);                   // bpp
                w.Write(frames[i].Length);
                w.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) w.Write(frame);
        }
        Console.WriteLine("wrote " + args[0]);
    }

    private static byte[] ToPng(Bitmap bmp)
    {
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // 32bpp BGRA DIB icon frame: BITMAPINFOHEADER (height doubled for the
    // AND mask), bottom-up XOR pixels, then an all-zero AND mask (alpha
    // channel carries transparency at 32bpp).
    private static byte[] ToDib(Bitmap bmp)
    {
        int s = bmp.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40);              // biSize
            w.Write(s);               // biWidth
            w.Write(s * 2);           // biHeight (XOR + AND)
            w.Write((short)1);        // biPlanes
            w.Write((short)32);       // biBitCount
            w.Write(0);               // biCompression
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);

            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                    w.Write(bmp.GetPixel(x, y).ToArgb());

            int maskRow = ((s + 31) / 32) * 4;   // 1bpp rows padded to 32 bits
            var zeros = new byte[maskRow];
            for (int y = 0; y < s; y++) w.Write(zeros);
            return ms.ToArray();
        }
    }

    // Scaled version of AppIcons.Draw: rounded speech bubble + tail + three
    // typing dots, in the app's accent blue with a subtle darker rim.
    private static Bitmap Draw(int s)
    {
        float f = s / 32f;
        var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var fill = Color.FromArgb(0x25, 0x63, 0xEB);
            var rim = Color.FromArgb(0x1A, 0x4C, 0xBF);

            using (var body = new GraphicsPath())
            {
                float x = 2 * f, y = 3 * f, wd = 28 * f, ht = 20 * f, d = 10 * f;
                body.AddArc(x, y, d, d, 180, 90);
                body.AddArc(x + wd - d, y, d, d, 270, 90);
                body.AddArc(x + wd - d, y + ht - d, d, d, 0, 90);
                body.AddArc(x, y + ht - d, d, d, 90, 90);
                body.CloseFigure();
                body.AddPolygon(new[]
                {
                    new PointF(8 * f, 22 * f),
                    new PointF(8 * f, 30 * f),
                    new PointF(16 * f, 22 * f),
                });
                using (var b = new SolidBrush(fill)) g.FillPath(b, body);
                if (s >= 24)
                    using (var p = new Pen(rim, Math.Max(1f, f * 0.8f))) g.DrawPath(p, body);
            }
            using (var b = new SolidBrush(Color.White))
            {
                float dot = 4 * f;
                g.FillEllipse(b, 7 * f, 11 * f, dot, dot);
                g.FillEllipse(b, 14 * f, 11 * f, dot, dot);
                g.FillEllipse(b, 21 * f, 11 * f, dot, dot);
            }
        }
        return bmp;
    }
}
