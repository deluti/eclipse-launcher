using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Eclipse;

internal sealed class GalleryPage : UserControl
{
    private readonly string directory;
    private readonly FlowLayoutPanel grid = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 8, 16, 16), WrapContents = true };
    private readonly Label empty = PixelTheme.TextLabel("Пока нет скриншотов", 18);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action<string> report;
    private bool loading;
    public GalleryPage(AppPaths paths, Action<string> report)
    {
        this.report = report;
        directory = Path.Combine(paths.Game, "screenshots");
        BackColor = PixelTheme.Background;
        var bar = new Panel { Dock = DockStyle.Top, Height = 58 };
        var title = PixelTheme.TextLabel("ПОСЛЕДНИЕ СКРИНШОТЫ", 16); title.Location = new Point(24, 19);
        var refresh = new PixelButton { Text = "Обновить", Width = 140, Dock = DockStyle.Right };
        var folder = new PixelButton { Text = "Папка", Width = 110, Dock = DockStyle.Right };
        refresh.Click += async (_, _) => await ReloadAsync();
        folder.Click += (_, _) => { Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); };
        bar.Controls.Add(title); bar.Controls.Add(folder); bar.Controls.Add(refresh);
        empty.ForeColor = PixelTheme.Muted;
        empty.Visible = false;
        Controls.Add(grid); Controls.Add(empty); Controls.Add(bar);
        Resize += (_, _) => empty.Location = new Point((Width - empty.Width) / 2, Height / 2);
        empty.BringToFront();
    }
    public async Task ReloadAsync()
    {
        if (loading || IsDisposed) return;
        loading = true;
        try
        {
            var files = await Task.Run(() => Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(120).Select(f => f.FullName).ToArray() : [], lifetime.Token);
            foreach (Control item in grid.Controls.Cast<Control>().ToArray()) item.Dispose();
            grid.Controls.Clear();
            empty.Visible = files.Length == 0;
            foreach (var file in files)
            {
                var thumb = await Task.Run(() => ReadThumbnail(file), lifetime.Token);
                if (IsDisposed || lifetime.IsCancellationRequested) { thumb?.Dispose(); return; }
                if (thumb == null) continue;
                var tile = new ScreenshotTile(file, thumb) { Margin = new Padding(8) };
                var index = Array.IndexOf(files, file);
                tile.Click += (_, _) => { using var viewer = new ScreenshotViewer(files, index); viewer.ShowDialog(FindForm()); };
                grid.Controls.Add(tile);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report(ex.Message); }
        finally { loading = false; }
    }
    private static Bitmap? ReadThumbnail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var original = Image.FromStream(stream);
            var thumb = new Bitmap(250, 141);
            using var g = Graphics.FromImage(thumb);
            g.Clear(PixelTheme.Panel);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var ratio = Math.Min(250f / original.Width, 141f / original.Height);
            var width = original.Width * ratio; var height = original.Height * ratio;
            g.DrawImage(original, (250 - width) / 2, (141 - height) / 2, width, height);
            return thumb;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OutOfMemoryException) { return null; }
    }
    protected override void Dispose(bool disposing) { if (disposing) { lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}

internal sealed class ScreenshotTile(string path, Bitmap thumbnail) : Control
{
    private bool over;
    protected override void OnClick(EventArgs e) { UiSounds.Play(UiCue.Confirm); base.OnClick(e); }
    protected override void OnCreateControl() { base.OnCreateControl(); Size = new Size(258, 185); DoubleBuffered = true; Cursor = Cursors.Hand; AccessibleName = Path.GetFileName(path); }
    protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { over = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        PixelTheme.Prepare(e.Graphics);
        PixelTheme.Box(e.Graphics, ClientRectangle, over ? PixelTheme.Line : PixelTheme.Panel);
        e.Graphics.DrawImageUnscaled(thumbnail, 4, 4);
        PixelTheme.Label(e.Graphics, File.GetLastWriteTime(path).ToString("dd.MM.yyyy   HH:mm"), new Rectangle(10, 147, Width - 20, 30), 12, over ? PixelTheme.Text : PixelTheme.Muted);
    }
    protected override void Dispose(bool disposing) { if (disposing) thumbnail.Dispose(); base.Dispose(disposing); }
}

internal sealed class ImageViewport : Control
{
    private Bitmap? image;
    private float zoom = 1;
    private PointF offset;
    private Point last;
    public ImageViewport()
    {
        DoubleBuffered = true; BackColor = Color.FromArgb(10, 12, 10); Dock = DockStyle.Fill; Cursor = Cursors.SizeAll;
        MouseWheel += (_, e) => { var previous = zoom; zoom = Math.Clamp(zoom + e.Delta / 120f * .2f, 1, 8); if (zoom != previous) UiSounds.Play(UiCue.Tick); Invalidate(); };
        MouseDown += (_, e) => { last = e.Location; Capture = true; Focus(); UiSounds.Play(UiCue.Click); };
        MouseMove += (_, e) => { if (Capture) { offset.X += e.X - last.X; offset.Y += e.Y - last.Y; last = e.Location; Invalidate(); } };
        MouseUp += (_, _) => { if (Capture) UiSounds.Play(UiCue.Back); Capture = false; };
        MouseDoubleClick += (_, _) => { UiSounds.Play(UiCue.Confirm); zoom = 1; offset = PointF.Empty; Invalidate(); };
    }
    public void LoadImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var original = Image.FromStream(stream);
        var next = new Bitmap(original);
        image?.Dispose(); image = next; zoom = 1; offset = PointF.Empty; Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (image == null) return;
        var ratio = Math.Min((float)Width / image.Width, (float)Height / image.Height) * zoom;
        e.Graphics.InterpolationMode = zoom > 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        var w = image.Width * ratio; var h = image.Height * ratio;
        e.Graphics.DrawImage(image, (Width - w) / 2 + offset.X, (Height - h) / 2 + offset.Y, w, h);
    }
    protected override void Dispose(bool disposing) { if (disposing) image?.Dispose(); base.Dispose(disposing); }
}

internal sealed class ScreenshotViewer : Form
{
    private readonly string[] files;
    private int index;
    private readonly ImageViewport viewport = new();
    private readonly Label caption = PixelTheme.TextLabel("");
    public ScreenshotViewer(string[] files, int index)
    {
        this.files = files; this.index = index;
        Text = "Скриншоты"; Font = PixelTheme.Font(); BackColor = PixelTheme.Background; ForeColor = PixelTheme.Text;
        Size = new Size(1100, 760); MinimumSize = new Size(640, 440); StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(10) };
        var previous = new PixelButton { Text = "<", Dock = DockStyle.Left, Width = 65 };
        var next = new PixelButton { Text = ">", Dock = DockStyle.Right, Width = 65 };
        caption.Dock = DockStyle.Fill; caption.AutoSize = false; caption.TextAlign = ContentAlignment.MiddleCenter;
        previous.Click += (_, _) => ShowImage(-1); next.Click += (_, _) => ShowImage(1);
        bar.Controls.Add(caption); bar.Controls.Add(previous); bar.Controls.Add(next);
        Controls.Add(viewport); Controls.Add(bar);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { UiSounds.Play(UiCue.Back); Close(); }
            if (e.KeyCode is Keys.Left or Keys.Right) { UiSounds.Play(UiCue.Select); ShowImage(e.KeyCode == Keys.Left ? -1 : 1); }
        };
        ShowImage(0);
    }
    private void ShowImage(int delta)
    {
        index = (index + delta + files.Length) % files.Length;
        try { viewport.LoadImage(files[index]); caption.Text = $"{index + 1} / {files.Length}   {Path.GetFileName(files[index])}"; }
        catch (Exception ex) when (ex is IOException or ArgumentException) { caption.Text = "Не удалось открыть скриншот"; }
    }
}
