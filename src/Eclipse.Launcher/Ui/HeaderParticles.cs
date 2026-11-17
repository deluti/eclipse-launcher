namespace Eclipse;

internal sealed class HeaderParticles : IDisposable
{
    // Coordinates belong to the navigation, but the field continues into the scene.
    internal const int EffectTop = 96;
    internal const int BandHeight = 164;
    internal const float MaximumLifetime = 4.2f;
    private struct Particle
    {
        internal float X, Y, Vx, Vy, Age, Lifetime;
        internal int Size, Tint;
    }
    private readonly Particle[] particles = new Particle[128];
    private readonly Random random;
    private readonly SolidBrush[,] brushes = new SolidBrush[3, 17];
    private float emission;
    internal int ActiveCount => particles.Count(p => p.Lifetime > 0);
    internal HeaderParticles(int seed = 7241)
    {
        random = new Random(seed);
        Color[] colors = [PixelTheme.Atmosphere, Color.FromArgb(97, 146, 61), Color.FromArgb(83, 129, 52)];
        for (var tint = 0; tint < 3; tint++)
        for (var alpha = 0; alpha < 17; alpha++) brushes[tint, alpha] = new SolidBrush(Color.FromArgb(alpha * 14, colors[tint]));
    }
    internal Rectangle Band(int width, int height) => new(0, Math.Min(EffectTop, height), width, Math.Max(0, Math.Min(BandHeight, height - EffectTop)));
    internal void Advance(double elapsed, int width, int height, bool emit = true)
    {
        var dt = (float)Math.Clamp(elapsed, 0, .05);
        var bottom = Math.Min(height - 2, EffectTop + BandHeight);
        for (var i = 0; i < particles.Length; i++)
        {
            ref var particle = ref particles[i];
            if (particle.Lifetime <= 0) continue;
            particle.Age += dt;
            particle.X += particle.Vx * dt; particle.Y += particle.Vy * dt; particle.Vy += 1.5f * dt;
            if (particle.Age >= particle.Lifetime || particle.Y + particle.Size > bottom || particle.X < 0 || particle.X > width - particle.Size)
                particle.Lifetime = 0;
        }
        if (!emit || width < 40 || bottom < EffectTop + 8) { emission = 0; return; }
        emission += dt * Math.Clamp(width / 55f, 12, 28);
        while (emission >= 1)
        {
            emission--;
            var index = Array.FindIndex(particles, p => p.Lifetime <= 0);
            if (index < 0) break;
            particles[index] = new Particle
            {
                X = 18 + random.NextSingle() * (width - 36), Y = EffectTop + 3 + random.NextSingle() * 5,
                Vx = (random.NextSingle() - .5f) * 8, Vy = 30 + random.NextSingle() * 12,
                Lifetime = 3f + random.NextSingle() * (MaximumLifetime - 3f), Size = random.Next(2, 5), Tint = random.Next(3)
            };
        }
    }
    internal IEnumerable<RectangleF> LiveBounds => particles.Where(p => p.Lifetime > 0).Select(p => new RectangleF(p.X, p.Y, p.Size, p.Size));
    internal void Draw(Graphics graphics, int width, int height)
    {
        var state = graphics.Save();
        graphics.SetClip(Band(width, height), System.Drawing.Drawing2D.CombineMode.Intersect);
        var clip = graphics.VisibleClipBounds;
        foreach (var particle in particles)
        {
            if (particle.Lifetime <= 0) continue;
            if (particle.Y + particle.Size + 1 < clip.Top || particle.Y - 1 > clip.Bottom || particle.X + particle.Size + 1 < clip.Left || particle.X - 1 > clip.Right) continue;
            var fraction = particle.Age / particle.Lifetime;
            var appear = Math.Min(1, particle.Age / .15f);
            var fade = Math.Min(1, (1 - fraction) / .4f);
            var edgeFade = Math.Clamp((Math.Min(height, EffectTop + BandHeight) - particle.Y) / 32f, 0, 1);
            var alpha = Math.Clamp((int)(16 * appear * fade * edgeFade), 0, 16);
            if (alpha == 0) continue;
            var size = fraction > .72f ? Math.Max(1, particle.Size - 1) : particle.Size;
            var x = (int)particle.X; var y = (int)particle.Y;
            graphics.FillRectangle(brushes[particle.Tint, Math.Max(1, alpha / 5)], x - 1, y - 1, size + 2, size + 2);
            graphics.FillRectangle(brushes[particle.Tint, alpha], x, y, size, size);
        }
        graphics.Restore(state);
    }
    public void Dispose() { foreach (var brush in brushes) brush.Dispose(); }
}
