namespace Eclipse;

internal enum LauncherTab { Screenshots, Files, Eclipse, Skins, Settings }
internal readonly record struct HeaderCard(LauncherTab Tab, Rectangle Bounds, float Depth);

internal sealed class TabCarousel : Control
{
    private static readonly string[] Names = ["Скриншоты", "Файлы", "Eclipse", "Скины", "Настройки"];
    internal void RefreshLanguage() { ClearComposition(); Invalidate(); }
    internal const int BaseCardHeight = 100;
    internal const int DefaultHeight = 176;
    internal const int DarkHeaderEnd = 130;
    private readonly HeaderParticles particles = new();
    private AnimationClock? clock;
    private bool suspended;
    internal void SuspendAnimation(bool value)
    {
        suspended = value;
        if (value) { clock?.Dispose(); clock = null; ClearComposition(); backdrop?.Dispose(); backdrop = null; }
        UpdateActivity();
    }
    private Form? host;
    private bool moving;
    private float position = 2;
    private float destination = 2;
    private int focus = 2;
    private Bitmap? backdrop;
    private Bitmap? composition;
    public LauncherTab ActiveTab { get; private set; } = LauncherTab.Eclipse;
    public LauncherTab FocusedTab => (LauncherTab)focus;
    internal IReadOnlyList<HeaderCard> CurrentCards => ArrangeCards(Width, position);
    internal bool IsAnimating => moving;
    internal bool AnimationRunning => clock?.IsRunning == true;
    internal Exception? AnimationFailure => clock?.Failure;
    internal long PaintCount { get; private set; }
    internal int ParticleCount => particles.ActiveCount;
    public event Action<LauncherTab>? Activated;
    internal event Action<double>? AtmosphereChanged;
    internal event Action? FrameStarting;
    internal Action<Graphics, Rectangle>? PaintBackdrop;

    public TabCarousel()
    {
        DoubleBuffered = true; ResizeRedraw = true; Height = DefaultHeight;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);
        BackColor = PixelTheme.Header; AccessibleName = L.T("Вкладки: A и D, Enter"); Cursor = Cursors.Hand;
        MouseClick += (_, e) =>
        {
            if (e.X < 44) { MoveSelection(-1); return; }
            if (e.X > Width - 44) { MoveSelection(1); return; }
            // Hit-testing uses the same Z order as painting: front cards own overlapping pixels.
            foreach (var card in CurrentCards.OrderBy(card => card.Depth))
                if (card.Bounds.Contains(e.Location)) { SetFocus((int)card.Tab, false); Commit(); break; }
        };
    }
    public void MoveSelection(int direction) => SetFocus((focus + direction % 5 + 5) % 5);
    internal void Open(LauncherTab tab) { SetFocus((int)tab, false); Commit(); }
    private void SetFocus(int value, bool sound = true)
    {
        var steps = (value - focus + 7) % 5 - 2;
        destination += steps; focus = value;
        if (steps != 0) { moving = true; if (sound) UiSounds.Play(UiCue.Select); }
        ClearComposition();
        Invalidate();
    }
    public void Commit()
    {
        UiSounds.Play(UiCue.Confirm);
        ActiveTab = (LauncherTab)focus; Activated?.Invoke(ActiveTab); ClearComposition(); Invalidate();
    }
    internal static IReadOnlyList<HeaderCard> ArrangeCards(int width, float center)
    {
        const int cardWidth = 236;
        var spacing = Math.Min(192f, Math.Max(1, (width - 100 - cardWidth) / 4f));
        var cards = new List<HeaderCard>(5);
        for (var i = 0; i < 5; i++)
        {
            var offset = i - center;
            offset -= 5 * MathF.Floor((offset + 2.5f) / 5);
            var distance = Math.Abs(offset);
            // The far card keeps its base height. Nearer cards grow down from one fixed top edge.
            var growth = Math.Clamp(2 - distance, 0, 2);
            var height = BaseCardHeight + (int)MathF.Round(growth * 8);
            var x = (int)MathF.Round(width / 2f + offset * spacing - cardWidth / 2f);
            cards.Add(new((LauncherTab)i, new Rectangle(x, 8, cardWidth, height), distance));
        }
        return cards;
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        backdrop?.Dispose(); backdrop = null; ClearComposition(); base.OnSizeChanged(e);
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AttachHost(); UpdateActivity();
    }
    protected override void OnHandleDestroyed(EventArgs e) { clock?.SetActive(false); base.OnHandleDestroyed(e); }
    protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); AttachHost(); UpdateActivity(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateActivity(); }
    private void AttachHost()
    {
        var next = FindForm();
        if (host == next) return;
        if (host != null) { host.Resize -= HostChanged; host.VisibleChanged -= HostChanged; }
        host = next;
        if (host != null) { host.Resize += HostChanged; host.VisibleChanged += HostChanged; }
    }
    private void HostChanged(object? sender, EventArgs e) => UpdateActivity();
    private void UpdateActivity()
    {
        var active = !suspended && IsHandleCreated && !IsDisposed && Visible && host?.Visible == true && host.WindowState != FormWindowState.Minimized;
        if (active) clock ??= new AnimationClock(this, OnFrame);
        clock?.SetActive(active);
    }
    private void OnFrame(double elapsed)
    {
        if (!Visible || host?.WindowState == FormWindowState.Minimized) { UpdateActivity(); return; }
        FrameStarting?.Invoke();
        var redrawCards = moving;
        if (moving)
        {
            position += (destination - position) * (float)(1 - Math.Exp(-23 * elapsed));
            if (Math.Abs(destination - position) < .007f) { position = destination = focus; moving = false; }
            ClearComposition();
        }
        particles.Advance(elapsed, Width, HeaderParticles.EffectTop + HeaderParticles.BandHeight);
        Invalidate(redrawCards ? ClientRectangle : particles.Band(Width, Height));
        Update();
        AtmosphereChanged?.Invoke(elapsed);
    }
    private void ClearComposition() { composition?.Dispose(); composition = null; }
    private Bitmap Compose()
    {
        var bitmap = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        PixelTheme.Prepare(graphics); DrawHeader(graphics); DrawCards(graphics);
        return bitmap;
    }
    private static Bitmap CreateBackdrop(int width, int height)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        const int pixel = 6;
        var brushes = new SolidBrush[25];
        for (var glow = 0; glow < 25; glow++)
            brushes[glow] = new SolidBrush(Color.FromArgb((int)(255 * glow / 100f), PixelTheme.Atmosphere));
        try
        {
            for (var y = 0; y < height; y += pixel)
            {
                for (var x = 0; x < width; x += pixel)
                {
                    var noise = PixelNoise(x / pixel, y / pixel);
                    // A broad bell curve has no visible start line. The opaque header masks its top.
                    var glowFalloff = Math.Exp(-Math.Pow((y - 108f) / 48f, 2));
                    var glow = Math.Clamp((int)(24 * glowFalloff * (.65 + noise * .35)), 0, 24);
                    g.FillRectangle(brushes[glow], x, y, pixel, pixel);
                }
            }
        }
        finally { foreach (var brush in brushes) brush.Dispose(); }
        return bitmap;
    }
    private static float PixelNoise(int x, int y)
    {
        var hash = unchecked((uint)((x + 19) * 73856093 ^ (y + 37) * 19349663));
        hash ^= hash >> 16; hash = unchecked(hash * 0x7feb352d);
        hash ^= hash >> 15; hash = unchecked(hash * 0x846ca68b); hash ^= hash >> 16;
        return (hash & 65535) / 65536f;
    }
    private void DrawHeader(Graphics graphics)
    {
        using var dark = new SolidBrush(PixelTheme.Header);
        graphics.FillRectangle(dark, 0, 0, Width, 84);
        const int pixel = 6;
        for (var y = 84; y < DarkHeaderEnd; y += pixel)
        {
            var t = Math.Clamp((y - 84f) / (DarkHeaderEnd - 84), 0, 1);
            t = t * t * (3 - 2 * t);
            for (var x = 0; x < Width; x += pixel)
                if (PixelNoise(x / pixel, y / pixel) >= t)
                    graphics.FillRectangle(dark, x, y, pixel, Math.Min(pixel, DarkHeaderEnd - y));
        }
    }
    internal void DrawAtmosphere(Graphics graphics, Point origin, Rectangle clip, bool includeParticles = true)
    {
        if (Width <= 0) return;
        var fieldHeight = HeaderParticles.EffectTop + HeaderParticles.BandHeight;
        backdrop ??= CreateBackdrop(Width, fieldHeight);
        var state = graphics.Save();
        graphics.TranslateTransform(-origin.X, -origin.Y);
        var source = Rectangle.Intersect(new Rectangle(clip.X + origin.X, clip.Y + origin.Y, clip.Width, clip.Height), new Rectangle(0, 0, Width, fieldHeight));
        if (!source.IsEmpty) graphics.DrawImage(backdrop, source, source, GraphicsUnit.Pixel);
        if (includeParticles) particles.Draw(graphics, Width, fieldHeight);
        graphics.Restore(state);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        composition ??= Compose();
        PixelTheme.Prepare(e.Graphics);
        var clip = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
        e.Graphics.Clear(PixelTheme.Background);
        PaintBackdrop?.Invoke(e.Graphics, clip);
        DrawAtmosphere(e.Graphics, Point.Empty, clip);
        e.Graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
        e.Graphics.DrawImage(composition, clip, clip, GraphicsUnit.Pixel);
        PaintCount++;
    }
    private void DrawCards(Graphics g)
    {
        foreach (var card in CurrentCards.OrderByDescending(card => card.Depth))
        {
            var depth = Math.Min(2, card.Depth);
            var prominence = 1 - depth * .33f;
            var edge = Math.Clamp((2.5f - card.Depth) * 2, 0, 1);
            var r = card.Bounds;
            var shadowSize = (int)(3 + (2 - depth) * 2);
            using (var shadow = new SolidBrush(Color.FromArgb((int)(110 * edge), 0, 0, 0)))
                g.FillRectangle(shadow, r.X + shadowSize, r.Y + shadowSize, r.Width, r.Height);
            var fill = PixelTheme.Blend(Color.FromArgb(24, 28, 24), Color.FromArgb(54, 65, 45), (2 - depth) / 2);
            fill = PixelTheme.Blend(PixelTheme.Header, fill, edge);
            PixelTheme.Box(g, r, fill);
            var color = PixelTheme.Blend(Color.FromArgb(94, 105, 87), PixelTheme.Text, (prominence - .34f) / .66f);
            color = PixelTheme.Blend(fill, color, edge);
            var growth = 2 - depth;
            var iconSize = 32 + (int)(growth * 8);
            PixelTheme.Icon(g, (int)card.Tab, new Rectangle(r.X, r.Y + 16, r.Width, iconSize), color);
            PixelTheme.Label(g, L.T(Names[(int)card.Tab]), new Rectangle(r.X + 18, r.Bottom - 41, r.Width - 36, 32), 14 + growth * 3, color, StringAlignment.Center);
            if ((int)card.Tab == focus)
            {
                using var accent = new SolidBrush(PixelTheme.Accent);
                g.FillRectangle(accent, r.Left + 2, r.Bottom - 4, r.Width - 4, 3);
            }
        }
        PixelTheme.Label(g, "< A", new Rectangle(6, 38, 38, 36), 12, PixelTheme.Muted, StringAlignment.Center);
        PixelTheme.Label(g, "D >", new Rectangle(Width - 44, 38, 38, 36), 12, PixelTheme.Muted, StringAlignment.Center);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (host != null) { host.Resize -= HostChanged; host.VisibleChanged -= HostChanged; }
            clock?.Dispose(); particles.Dispose(); backdrop?.Dispose(); composition?.Dispose();
        }
        base.Dispose(disposing);
    }
}
