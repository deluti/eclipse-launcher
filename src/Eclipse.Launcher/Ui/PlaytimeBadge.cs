namespace Eclipse;

internal sealed class PlaytimeBadge
{
    private string caption = PlaytimeCounter.Format(0);
    internal Rectangle Bounds { get; set; }
    internal string AccessibleName => L.T("Время в Minecraft: ") + caption;
    internal bool SetHours(double hours)
    {
        var next = PlaytimeCounter.Format(hours);
        if (caption == next) return false;
        caption = next; return true;
    }
    internal void Draw(Graphics graphics)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(Bounds.Left, Bounds.Top);
        var g = graphics;
        PixelTheme.Prepare(g);
        PixelTheme.Box(g, new Rectangle(0, 0, Bounds.Width, Bounds.Height - 2), PixelTheme.Panel, false, 2);
        using (var shadow = new SolidBrush(Color.FromArgb(10, 14, 10))) g.FillRectangle(shadow, 3, 2, Bounds.Width - 6, 12);
        var textWidth = (int)Math.Ceiling(g.MeasureString(caption, PixelTheme.Font(15)).Width);
        var left = (Bounds.Width - textWidth - 26) / 2;
        string[] clock = ["..####..", ".#....#.", "#..#...#", "#..#...#", "#..###.#", "#......#", ".#....#.", "..####.."];
        using var pixels = new SolidBrush(PixelTheme.Accent);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            if (clock[y][x] == '#') g.FillRectangle(pixels, left + x * 2, 20 + y * 2, 2, 2);
        PixelTheme.Label(g, caption, new Rectangle(left + 26, 15, textWidth + 4, 27), 15, PixelTheme.Text);
        graphics.Restore(state);
    }
}
