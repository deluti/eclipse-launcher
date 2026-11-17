using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Eclipse;

internal static class PixelTheme
{
    public static readonly Color Background = Color.FromArgb(17, 19, 18);
    public static readonly Color Header = Color.FromArgb(7, 9, 8);
    public static readonly Color Panel = Color.FromArgb(25, 28, 26);
    public static readonly Color Surface = Color.FromArgb(36, 40, 36);
    public static readonly Color Line = Color.FromArgb(57, 63, 56);
    public static readonly Color Text = Color.FromArgb(232, 235, 219);
    public static readonly Color Muted = Color.FromArgb(123, 135, 120);
    public static readonly Color Accent = Color.FromArgb(173, 205, 132);
    public static readonly Color Atmosphere = Color.FromArgb(71, 111, 45);
    private static readonly PrivateFontCollection Fonts = new();
    private static readonly Dictionary<float, Font> CachedFonts = new();
    private static readonly IntPtr FontMemory;

    [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(IntPtr memory, uint size, IntPtr reserved, ref uint count);
    static PixelTheme()
    {
        using var stream = typeof(PixelTheme).Assembly.GetManifestResourceStream("Eclipse.Assets.Monocraft.ttf")!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        FontMemory = Marshal.AllocCoTaskMem(bytes.Length);
        Marshal.Copy(bytes, 0, FontMemory, bytes.Length);
        Fonts.AddMemoryFont(FontMemory, bytes.Length);
        uint count = 0;
        _ = AddFontMemResourceEx(FontMemory, (uint)bytes.Length, IntPtr.Zero, ref count);
    }
    public static Font Font(float pixels = 14)
    {
        if (!CachedFonts.TryGetValue(pixels, out var font)) CachedFonts[pixels] = font = new Font(Fonts.Families[0], pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        return font;
    }
    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
    }
    public static void Label(Graphics g, string text, Rectangle bounds, float size, Color color, StringAlignment align = StringAlignment.Near)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, Font(size), brush, bounds, format);
    }
    public static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
    public static void Box(Graphics g, Rectangle r, Color fill, bool raised = true, int thickness = 2)
    {
        using var baseBrush = new SolidBrush(Color.FromArgb(7, 9, 7));
        g.FillRectangle(baseBrush, r);
        r.Inflate(-thickness, -thickness);
        using var brush = new SolidBrush(fill);
        g.FillRectangle(brush, r);
        using var light = new Pen(Blend(fill, Color.White, raised ? .18f : .05f), thickness);
        using var dark = new Pen(Blend(fill, Color.Black, .45f), thickness);
        g.DrawLine(light, r.Left + 1, r.Top + 1, r.Right - 2, r.Top + 1);
        g.DrawLine(light, r.Left + 1, r.Top + 1, r.Left + 1, r.Bottom - 2);
        g.DrawLine(dark, r.Left + 1, r.Bottom - 2, r.Right - 2, r.Bottom - 2);
        g.DrawLine(dark, r.Right - 2, r.Top + 1, r.Right - 2, r.Bottom - 2);
    }
    public static Label TextLabel(string text, int size = 14) => new() { Text = text, AutoSize = true, ForeColor = Text, Font = Font(size), BackColor = Color.Transparent };
    public static void Style(Control control)
    {
        control.Font = Font();
        control.ForeColor = Text;
        control.BackColor = Panel;
    }
    public static void Icon(Graphics g, int icon, Rectangle r, Color color)
    {
        string[] pixels = icon switch
        {
            0 => ["................", "..############..", "..#..........#..", "..#..##......#..", "..#..##......#..", "..#..........#..", "..#.....##...#..", "..#....####..#..", "..#.#.######.#..", "..############..", "................", "....########....", "................", "................", "................", "................"],
            1 => ["................", "...######.......", "...#....##......", "...#....###.....", "...#......#.....", "...#......#.....", "...#.####.#.....", "...#......#.....", "...#.####.#.....", "...#......#.....", "...#.####.#.....", "...#......#.....", "...########.....", "................", "................", "................"],
            2 => ["................", "......####......", "....########....", "...###....###...", "..###......###..", "..##........##..", ".###........###.", ".###........###.", ".###........###.", "..##........##..", "..###......###..", "...###....###...", "....########....", "......####......", "................", "................"],
            3 => ["................", ".....######.....", ".....######.....", ".....######.....", "......####......", "...##########...", "...##########...", "...##########...", "...##########...", ".....######.....", ".....######.....", ".....##..##.....", ".....##..##.....", ".....##..##.....", "................", "................"],
            _ => ["................", "......####......", "...#..####..#...", "..####.##.####..", "..###########...", "...###....###...", ".####......####.", ".####......####.", "...###....###...", "..############..", "..####.##.####..", "...#..####..#...", "......####......", "................", "................", "................"]
        };
        var unit = Math.Max(1, Math.Min(r.Width, r.Height) / 16);
        var x0 = r.X + (r.Width - unit * 16) / 2;
        var y0 = r.Y + (r.Height - unit * 16) / 2;
        using var brush = new SolidBrush(color);
        for (var y = 0; y < pixels.Length; y++)
            for (var x = 0; x < pixels[y].Length; x++)
                if (pixels[y][x] == '#') g.FillRectangle(brush, x0 + x * unit, y0 + y * unit, unit, unit);
    }
}

internal sealed class PixelButton : Button
{
    private bool over;
    private bool down;
    [System.ComponentModel.DefaultValue(false)] public bool Primary { get; set; }
    [System.ComponentModel.DefaultValue(false)] public bool Quiet { get; set; }
    public PixelButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = PixelTheme.Font();
        ForeColor = PixelTheme.Text;
        BackColor = PixelTheme.Panel;
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        Size = new Size(160, 42);
        TabStop = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        // ButtonBase is opaque: WinForms does not clear its pooled buffer for us.
        // Paint every pixel even for quiet buttons, otherwise old captions/hover frames bleed through.
        e.Graphics.Clear(BackColor);
        PixelTheme.Prepare(e.Graphics);
        var color = Primary ? Color.FromArgb(62, 127, 39) : PixelTheme.Surface;
        if (over) color = PixelTheme.Blend(color, Color.White, .10f);
        if (down) color = PixelTheme.Blend(color, Color.Black, .15f);
        if (!Enabled) color = PixelTheme.Blend(color, PixelTheme.Background, .65f);
        if (!Quiet || over || down) PixelTheme.Box(e.Graphics, new Rectangle(0, 0, Width, Height - 2), color, !down, Primary ? 3 : 2);
        PixelTheme.Label(e.Graphics, Text, new Rectangle(4, down ? 4 : 1, Width - 8, Height - 5), Font.Size, Enabled ? ForeColor : PixelTheme.Muted, StringAlignment.Center);
        if (Focused) { using var pen = new Pen(PixelTheme.Accent); e.Graphics.DrawRectangle(pen, 5, 5, Width - 11, Height - 13); }
    }
    protected override void OnClick(EventArgs e) { if (Enabled) UiSounds.Play(Primary ? UiCue.Confirm : UiCue.Click); base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

internal sealed class PixelComboBox : Control
{
    private int selectedIndex = -1;
    private ToolStripDropDown? popup;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal List<object> Items { get; } = [];
    [System.ComponentModel.DefaultValue(-1)]
    internal int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var next = value >= 0 && value < Items.Count ? value : -1;
            if (selectedIndex == next) return;
            selectedIndex = next; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal event EventHandler? SelectedIndexChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal object? SelectedItem { get => selectedIndex >= 0 && selectedIndex < Items.Count ? Items[selectedIndex] : null; set => SelectedIndex = value == null ? -1 : Items.IndexOf(value); }
    internal bool DroppedDown => popup?.Visible == true;
    public PixelComboBox()
    {
        DoubleBuffered = true; TabStop = true; Cursor = Cursors.Hand; Height = 36;
        PixelTheme.Style(this); AccessibleRole = AccessibleRole.ComboBox;
        MouseClick += (_, _) => Open();
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Space or Keys.Down) { Open(); e.Handled = true; } };
    }
    public void BeginUpdate() { }
    public void EndUpdate() => Invalidate();
    protected override void OnPaint(PaintEventArgs e)
    {
        PixelTheme.Prepare(e.Graphics);
        PixelTheme.Box(e.Graphics, ClientRectangle, PixelTheme.Panel);
        PixelTheme.Label(e.Graphics, SelectedItem?.ToString() ?? "—", new Rectangle(14, 0, Width - 48, Height), 14, Enabled ? PixelTheme.Text : PixelTheme.Muted);
        using var brush = new SolidBrush(Enabled ? PixelTheme.Text : PixelTheme.Muted);
        var x = Width - 24; var y = Height / 2 - 2;
        e.Graphics.FillRectangle(brush, x, y, 10, 2); e.Graphics.FillRectangle(brush, x + 2, y + 2, 6, 2); e.Graphics.FillRectangle(brush, x + 4, y + 4, 2, 2);
    }
    private void Open()
    {
        if (!Enabled || Items.Count == 0) return;
        if (DroppedDown) { UiSounds.Play(UiCue.Back); popup!.Close(); return; }
        UiSounds.Play(UiCue.Click);
        popup?.Dispose();
        var list = new ListBox { Font = PixelTheme.Font(), BackColor = PixelTheme.Panel, ForeColor = PixelTheme.Text, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 30, IntegralHeight = false, Size = new Size(Width - 4, Math.Min(8, Items.Count) * 30) };
        list.Items.AddRange(Items.ToArray()); list.SelectedIndex = selectedIndex;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            PixelTheme.Prepare(e.Graphics);
            var selected = e.State.HasFlag(DrawItemState.Selected);
            using var brush = new SolidBrush(selected ? PixelTheme.Line : PixelTheme.Panel); e.Graphics.FillRectangle(brush, e.Bounds);
            PixelTheme.Label(e.Graphics, Items[e.Index].ToString() ?? "", new Rectangle(12, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height), 14, selected ? PixelTheme.Accent : PixelTheme.Text);
        };
        var host = new ToolStripControlHost(list) { Padding = Padding.Empty, Margin = Padding.Empty, AutoSize = false, Size = list.Size };
        popup = new ToolStripDropDown { Padding = new Padding(2), Margin = Padding.Empty, BackColor = PixelTheme.Line, DropShadowEnabled = false };
        popup.Items.Add(host);
        void Commit() { if (list.SelectedIndex >= 0) SelectedIndex = list.SelectedIndex; UiSounds.Play(UiCue.Confirm); popup.Close(); }
        list.MouseClick += (_, e) => { if (list.IndexFromPoint(e.Location) >= 0) Commit(); };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; }
            if (e.KeyCode == Keys.Escape) { UiSounds.Play(UiCue.Back); popup.Close(); }
            if (e.KeyCode == Keys.Up && list.SelectedIndex > 0 || e.KeyCode == Keys.Down && list.SelectedIndex < list.Items.Count - 1) UiSounds.Play(UiCue.Select);
        };
        popup.Closed += (_, _) => { if (!IsDisposed) { Focus(); Invalidate(); } };
        popup.Show(this, new Point(0, Height + 2)); list.Focus();
    }
    protected override void Dispose(bool disposing) { if (disposing) popup?.Dispose(); base.Dispose(disposing); }
}
