using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Eclipse;

internal sealed class StevePreview : Control
{
    private readonly Bitmap skin;
    internal StevePreview()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Eclipse/Assets/steve.png"));
        skin = new Bitmap(stream);
        Width = 300;
        Height = 390;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width / 240d, Bounds.Height / 360d);
        var ox = (Bounds.Width - 160 * scale) / 2;
        var oy = Math.Max(8, (Bounds.Height - 336 * scale) / 2);
        var outline = new SolidColorBrush(Color.Parse("#101410"));
        void Part(Rect src, Rect dst)
        {
            var target = new Rect(ox + dst.X * scale, oy + dst.Y * scale, dst.Width * scale, dst.Height * scale);
            context.DrawRectangle(outline, null, target.Inflate(3));
            context.DrawImage(skin, src, target);
        }
        Part(new Rect(8, 8, 8, 8), new Rect(32, 0, 96, 96));
        Part(new Rect(20, 20, 8, 12), new Rect(32, 96, 96, 144));
        Part(new Rect(44, 20, 4, 12), new Rect(0, 96, 32, 144));
        Part(new Rect(36, 52, 4, 12), new Rect(128, 96, 32, 144));
        Part(new Rect(4, 20, 4, 12), new Rect(32, 240, 48, 96));
        Part(new Rect(20, 52, 4, 12), new Rect(80, 240, 48, 96));
    }

}
