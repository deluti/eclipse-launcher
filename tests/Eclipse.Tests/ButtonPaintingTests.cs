using System.Drawing.Imaging;
using System.Reflection;
using Eclipse;

internal static class ButtonPaintingTests
{
    private static readonly MethodInfo PaintMethod = typeof(PixelButton).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static void Run()
    {
        using var oldButton = new PixelButton { Text = "Обновить", Size = new Size(170, 40) };
        using var cancel = new PixelButton { Text = "Отмена", Quiet = true, BackColor = PixelTheme.Header, Size = oldButton.Size };
        using var reused = new Bitmap(170, 40);
        using var fresh = new Bitmap(170, 40);
        using (var g = Graphics.FromImage(reused))
        {
            g.Clear(Color.Magenta);
            Paint(oldButton, g);
            // WinForms reuses buffered drawing surfaces. The next opaque button must erase old pixels.
            Paint(cancel, g);
        }
        using (var g = Graphics.FromImage(fresh)) { g.Clear(cancel.BackColor); Paint(cancel, g); }
        Check(Equal(reused, fresh), "Quiet button kept another button's caption/background in the reused paint buffer");

        foreach (var quiet in new[] { false, true })
        foreach (var enabled in new[] { false, true })
        {
            cancel.Quiet = quiet; cancel.Enabled = enabled;
            using var dirty = new Bitmap(cancel.Width, cancel.Height);
            using (var g = Graphics.FromImage(dirty)) { g.Clear(Color.Magenta); Paint(cancel, g); }
            for (var y = 0; y < dirty.Height; y++)
            for (var x = 0; x < dirty.Width; x++)
                Check(dirty.GetPixel(x, y).ToArgb() != Color.Magenta.ToArgb(), "Button left unpainted pixels, including its bottom edge");
        }
        cancel.Quiet = true; cancel.Enabled = true;
        using var evolving = new Bitmap(cancel.Width, cancel.Height);
        using var expected = new Bitmap(cancel.Width, cancel.Height);
        using (var g = Graphics.FromImage(evolving))
        {
            g.Clear(cancel.BackColor); cancel.Text = "Обновить"; Paint(cancel, g);
            typeof(PixelButton).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cancel, [EventArgs.Empty]);
            Paint(cancel, g);
            cancel.Text = "Отмена";
            typeof(PixelButton).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cancel, [EventArgs.Empty]);
            Paint(cancel, g);
        }
        using (var g = Graphics.FromImage(expected)) { g.Clear(cancel.BackColor); Paint(cancel, g); }
        Check(Equal(evolving, expected), "Hover/text transitions must leave the same pixels as a fresh frame");
        Console.WriteLine("PASS paint: reused buffers, changing captions, hover, disabled state and full button coverage");
    }
    private static void Paint(PixelButton button, Graphics graphics)
    {
        using var args = new PaintEventArgs(graphics, button.ClientRectangle);
        PaintMethod.Invoke(button, [args]);
    }
    private static bool Equal(Bitmap left, Bitmap right)
    {
        using var a = new MemoryStream(); using var b = new MemoryStream();
        left.Save(a, ImageFormat.Png); right.Save(b, ImageFormat.Png);
        return a.ToArray().SequenceEqual(b.ToArray());
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
