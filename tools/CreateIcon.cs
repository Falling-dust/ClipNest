using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Creates a multi-resolution Windows icon using only framework libraries.
internal static class CreateIcon
{
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 1;
        int[] sizes = { 16, 32, 48, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);
                graphics.ScaleTransform(size / 64f, size / 64f);
                using (GraphicsPath background = RoundedRectangle(2, 2, 60, 60, 15))
                using (SolidBrush teal = new SolidBrush(Color.FromArgb(19, 118, 101)))
                    graphics.FillPath(teal, background);
                using (GraphicsPath page = RoundedRectangle(17, 15, 30, 39, 5))
                using (Pen outline = new Pen(Color.White, 3))
                    graphics.DrawPath(outline, page);
                using (GraphicsPath clip = RoundedRectangle(24, 10, 16, 10, 3))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(214, 244, 231)))
                    graphics.FillPath(fill, clip);
                using (Pen line = new Pen(Color.FromArgb(224, 246, 238), 3))
                {
                    line.StartCap = LineCap.Round;
                    line.EndCap = LineCap.Round;
                    graphics.DrawLine(line, 24, 31, 40, 31);
                    graphics.DrawLine(line, 24, 39, 40, 39);
                    graphics.DrawLine(line, 24, 47, 34, 47);
                }
                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    images.Add(stream.ToArray());
                }
            }
        }

        using (BinaryWriter writer = new BinaryWriter(File.Create(args[0])))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int index = 0; index < sizes.Length; index++)
            {
                byte dimension = sizes[index] == 256 ? (byte)0 : (byte)sizes[index];
                writer.Write(dimension);
                writer.Write(dimension);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[index].Length);
                writer.Write(offset);
                offset += images[index].Length;
            }
            foreach (byte[] image in images) writer.Write(image);
        }
        return 0;
    }

    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
