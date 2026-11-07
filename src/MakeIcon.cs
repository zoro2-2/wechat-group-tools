using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

// mkicon.exe <out.ico> -- generate a multi-size ICO for the app
class MkIcon
{
    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        int[] sizes = new int[] { 256, 64, 48, 32, 16 };
        var pngs = new List<byte[]>();

        foreach (int s in sizes)
        {
            using (var bmp = Render(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }

        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);            // reserved
            bw.Write((ushort)1);            // type = icon
            bw.Write((ushort)sizes.Length); // count

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));   // width
                bw.Write((byte)(s >= 256 ? 0 : s));   // height
                bw.Write((byte)0);                    // color count
                bw.Write((byte)0);                    // reserved
                bw.Write((ushort)1);                  // planes
                bw.Write((ushort)32);                 // bpp
                bw.Write((uint)pngs[i].Length);       // bytes in resource
                bw.Write((uint)offset);               // offset
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) bw.Write(p);
        }

        Console.WriteLine("written " + outPath + " sizes=" + string.Join(",", Array.ConvertAll(sizes, x => x.ToString())));
        return 0;
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // 圆角矩形背景 + 微信绿渐变
            float pad = size * 0.045f;
            float r = size * 0.22f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            using (var path = RoundRect(rect, r))
            using (var brush = new LinearGradientBrush(rect, Color.FromArgb(9, 214, 106), Color.FromArgb(4, 168, 84), 55f))
            {
                g.FillPath(brush, path);
            }

            // 右上角高光
            using (var hl = new LinearGradientBrush(rect, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            using (var path2 = RoundRect(new RectangleF(pad, pad, size - pad * 2, (size - pad * 2) * 0.55f), r))
            {
                g.FillPath(hl, path2);
            }

            // 中间文字「群」
            string txt = "群";
            float fontSize = size * 0.56f;
            Font font;
            try { font = new Font("Microsoft YaHei", fontSize, FontStyle.Bold, GraphicsUnit.Pixel); }
            catch { font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold, GraphicsUnit.Pixel); }

            using (font)
            using (var sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (var shadow = new SolidBrush(Color.FromArgb(50, 0, 0, 0)))
                {
                    var rShadow = new RectangleF(0, size * 0.02f, size, size);
                    g.DrawString(txt, font, shadow, rShadow, sf);
                }
                using (var white = new SolidBrush(Color.White))
                    g.DrawString(txt, font, white, new RectangleF(0, 0, size, size), sf);
            }
        }
        return bmp;
    }

    static GraphicsPath RoundRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
