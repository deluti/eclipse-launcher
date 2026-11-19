using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Eclipse;

// One window-wide bitmap keeps the pattern and lower fade aligned across child controls.
internal sealed class LauncherBackdrop : IDisposable
{
    private Bitmap? bitmap;
    private int fadeTop;

    internal void Draw(Graphics graphics, Point origin, Rectangle clip, Size size, int gradientTop)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        if (bitmap == null || bitmap.Size != size || fadeTop != gradientTop)
        {
            bitmap?.Dispose(); fadeTop = gradientTop;
            bitmap = Create(size, gradientTop);
        }
        var source = Rectangle.Intersect(new Rectangle(origin.X + clip.X, origin.Y + clip.Y, clip.Width, clip.Height), new Rectangle(Point.Empty, size));
        if (source.IsEmpty) return;
        var destination = new Rectangle(source.X - origin.X, source.Y - origin.Y, source.Width, source.Height);
        var mode = graphics.CompositingMode;
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.DrawImage(bitmap, destination, source, GraphicsUnit.Pixel);
        graphics.CompositingMode = mode;
    }

    private static Bitmap Create(Size size, int gradientTop)
    {
        var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        var bounds = new Rectangle(Point.Empty, size);
        var data = image.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = data.Stride / 4;
            var pixels = new int[stride * size.Height];
            var blue = PixelTheme.Background.B; var green = PixelTheme.Background.G; var red = PixelTheme.Background.R;
            const int grain = 3;
            for (var y = 0; y < size.Height; y += grain)
            {
                var row = y * stride;
                for (var x = 0; x < size.Width; x += grain)
                {
                    var hash = unchecked((uint)((x / grain + 19) * 73856093 ^ (y / grain + 37) * 19349663));
                    hash ^= hash >> 16; hash = unchecked(hash * 0x7feb352d);
                    hash ^= hash >> 15; hash = unchecked(hash * 0x846ca68b); hash ^= hash >> 16;
                    var shade = (int)(hash % 5) - 2;
                    var argb = unchecked((int)0xff000000) | (red + shade) << 16 | (green + shade) << 8 | (blue + shade);
                    pixels.AsSpan(row + x, Math.Min(grain, size.Width - x)).Fill(argb);
                }
                for (var py = y + 1; py < Math.Min(y + grain, size.Height); py++) Array.Copy(pixels, row, pixels, py * stride, size.Width);
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { image.UnlockBits(data); }
        using var graphics = Graphics.FromImage(image);
        PixelTheme.Prepare(graphics);
        const int pixel = 6;
        var shades = new SolidBrush[17];
        for (var level = 0; level < shades.Length; level++) shades[level] = new SolidBrush(Color.FromArgb(level * 10, 0, 0, 0));
        try
        {
            for (var y = Math.Max(0, gradientTop / pixel * pixel); y < size.Height; y += pixel)
            {
                var t = Math.Clamp((y - gradientTop) / (float)Math.Max(1, size.Height - gradientTop), 0, 1);
                var value = t * t * (3 - 2 * t) * 16;
                for (var x = 0; x < size.Width; x += pixel)
                {
                    var hash = unchecked((uint)((x / pixel + 17) * 73856093 ^ (y / pixel + 31) * 19349663));
                    hash ^= hash >> 16;
                    var noise = (hash & 255) / 256f;
                    var level = Math.Clamp((int)value + (value % 1 > noise ? 1 : 0), 0, 16);
                    if (level > 0) graphics.FillRectangle(shades[level], x, y, pixel, pixel);
                }
            }
        }
        finally { foreach (var shade in shades) shade.Dispose(); }
        return image;
    }

    public void Dispose() => bitmap?.Dispose();
}
