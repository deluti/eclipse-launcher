using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Eclipse;

internal sealed class SkinScene : Control
{
    private sealed record Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Bitmap Texture, SkinBone Bone)
    {
        internal readonly PointF[] Projection = new PointF[3];
        internal float Depth;
        internal bool Visible;
    }
    private static readonly IComparer<Face> DepthOrder = Comparer<Face>.Create((a, b) => a.Depth.CompareTo(b.Depth));
    private readonly List<Face> faces = [];
    private readonly SkinPoseRig rig = new((StandingPose)Random.Shared.Next(2), Random.Shared.NextSingle() * 2 * MathF.PI);
    internal StandingPose Pose => rig.Pose;
    internal double IdleSeconds => rig.Seconds;
    internal float HorizontalAnchor = .5f;
    private Face[] orderedFaces = [];
    private readonly Bitmap platform = CreatePlatform();
    private readonly PointF[] platformProjection = new PointF[3];
    private Bitmap skin = CreateDefaultSkin();
    private Bitmap? cape;
    private bool slim;
    private Point lastMouse;
    private float yaw = -.32f;
    private float pitch = .13f;
    private float zoom = 1f;
    private Bitmap? model;
    private bool modelDirty = true;
    private double idleFrameTime;
    private Rectangle modelBounds;
    internal bool FrameDriven;
    private Bitmap? characterBuffer, outlineBuffer;
    private byte[] characterPixels = [], outlinePixels = [];
    private static readonly Point[][] OutlineOffsets = Enumerable.Range(0, 6).Select(radius =>
        (from dy in Enumerable.Range(-radius, radius * 2 + 1)
         from dx in Enumerable.Range(-radius, radius * 2 + 1)
         where dx * dx + dy * dy <= radius * radius
         select new Point(dx, dy)).ToArray()).ToArray();
    internal long ModelFrames { get; private set; }
    internal long PlatformTicks, CharacterTicks, OutlineTicks;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<Graphics, Rectangle>? PaintAtmosphere { get; set; }
    public SkinScene()
    {
        DoubleBuffered = true;
        BackColor = PixelTheme.Background;
        Cursor = Cursors.SizeAll;
        AccessibleName = "3D-персонаж. Мышь — вращение, колесо — масштаб";
        ResizeRedraw = true;
        Rebuild();
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { lastMouse = e.Location; Capture = true; Focus(); UiSounds.Play(UiCue.Click); } };
        MouseMove += (_, e) =>
        {
            if (!Capture) return;
            yaw += (e.X - lastMouse.X) * .012f;
            pitch = Math.Clamp(pitch + (e.Y - lastMouse.Y) * .006f, -.15f, .68f);
            lastMouse = e.Location;
            ClearModel();
            RequestModelPaint();
        };
        MouseUp += (_, _) => { if (Capture) UiSounds.Play(UiCue.Back); Capture = false; };
        MouseWheel += (_, e) => { var previous = zoom; zoom = Math.Clamp(zoom + e.Delta / 120f * .12f, .65f, 1.8f); if (zoom != previous) UiSounds.Play(UiCue.Tick); ClearModel(); RequestModelPaint(); };
        MouseDoubleClick += (_, _) => { UiSounds.Play(UiCue.Confirm); yaw = -.32f; pitch = .13f; zoom = 1; ClearModel(); RequestModelPaint(); };
    }
    public void SetAppearance(Bitmap? newSkin, bool isSlim, Bitmap? newCape)
    {
        skin.Dispose();
        cape?.Dispose();
        skin = newSkin == null ? CreateDefaultSkin() : new Bitmap(newSkin);
        cape = newCape == null ? null : new Bitmap(newCape);
        slim = isSlim;
        rig.SetArmWidth(slim ? 3 : 4);
        Rebuild();
        ClearModel();
        RequestModelPaint();
    }
    private void Rebuild()
    {
        foreach (var face in faces) face.Texture.Dispose();
        faces.Clear();
        AddBox(0, 24, 0, 8, 8, 8, 0, 0, bone: SkinBone.Head);
        AddBox(0, 12, 0, 8, 12, 4, 16, 16);
        var arm = slim ? 3 : 4;
        AddLimb(-(4 + arm / 2f), 12, arm, 40, 16, SkinBone.RightUpperArm, SkinBone.RightForearm);
        AddLimb(4 + arm / 2f, 12, arm, skin.Height == 32 ? 40 : 32, skin.Height == 32 ? 16 : 48, SkinBone.LeftUpperArm, SkinBone.LeftForearm);
        AddLimb(-2, 0, 4, 0, 16, SkinBone.RightThigh, SkinBone.RightShin);
        AddLimb(2, 0, 4, skin.Height == 32 ? 0 : 16, skin.Height == 32 ? 16 : 48, SkinBone.LeftThigh, SkinBone.LeftShin);
        AddBox(0, 24, 0, 8, 8, 8, 32, 0, .5f, bone: SkinBone.Head);
        if (skin.Height >= 64)
        {
            AddBox(0, 12, 0, 8, 12, 4, 16, 32, .24f);
            AddLimb(-(4 + arm / 2f), 12, arm, 40, 32, SkinBone.RightUpperArm, SkinBone.RightForearm, .24f);
            AddLimb(4 + arm / 2f, 12, arm, 48, 48, SkinBone.LeftUpperArm, SkinBone.LeftForearm, .24f);
            AddLimb(-2, 0, 4, 0, 32, SkinBone.RightThigh, SkinBone.RightShin, .24f);
            AddLimb(2, 0, 4, 0, 48, SkinBone.LeftThigh, SkinBone.LeftShin, .24f);
        }
        if (cape != null) AddBox(SkinPoseRig.CapeAnchor.X, SkinPoseRig.CapeAnchor.Y - SkinPoseRig.CapeHeight,
            SkinPoseRig.CapeAnchor.Z, SkinPoseRig.CapeWidth, SkinPoseRig.CapeHeight, SkinPoseRig.CapeThickness, 0, 0, 0, cape, SkinBone.Cape);
        orderedFaces = faces.ToArray();
    }
    private void AddLimb(float x, float bottom, int width, int u, int v, SkinBone upper, SkinBone lower, float grow = 0)
    {
        AddBox(x, bottom + 6, 0, width, 6, 4, u, v, grow, bone: upper);
        AddBox(x, bottom, 0, width, 6, 4, u, v, grow, bone: lower, verticalOffset: 6);
    }
    private void AddBox(float cx, float bottom, float cz, int w, int h, int d, int u, int v, float grow = 0, Bitmap? image = null,
        SkinBone bone = SkinBone.Torso, int verticalOffset = 0)
    {
        var xl = cx - w / 2f - grow; var xr = cx + w / 2f + grow;
        var yb = bottom - grow; var yt = bottom + h + grow;
        var zf = cz + d / 2f + grow; var zb = cz - d / 2f - grow;
        var texture = image ?? skin;
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 n, int x, int y, int width, int height)
        {
            if (x + width > texture.Width || y + height > texture.Height) return;
            var crop = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(crop))
            {
                var brightness = .73f + Math.Clamp(n.Y * .18f + n.Z * .10f - n.X * .08f, -.16f, .25f);
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix00 = brightness, Matrix11 = brightness, Matrix22 = brightness, Matrix33 = 1, Matrix44 = 1 });
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(texture, new Rectangle(0, 0, width, height), x, y, width, height, GraphicsUnit.Pixel, attributes);
            }
            faces.Add(new(a, b, c, n, crop, bone));
        }
        Face(new(xl, yt, zf), new(xr, yt, zf), new(xl, yb, zf), new(0, 0, 1), u + d, v + d + verticalOffset, w, h);
        Face(new(xr, yt, zb), new(xl, yt, zb), new(xr, yb, zb), new(0, 0, -1), u + d + w + d, v + d + verticalOffset, w, h);
        Face(new(xl, yt, zb), new(xl, yt, zf), new(xl, yb, zb), new(-1, 0, 0), u, v + d + verticalOffset, d, h);
        Face(new(xr, yt, zf), new(xr, yt, zb), new(xr, yb, zf), new(1, 0, 0), u + d + w, v + d + verticalOffset, d, h);
        Face(new(xl, yt, zb), new(xr, yt, zb), new(xl, yt, zf), new(0, 1, 0), u + d, v, w, d);
        Face(new(xl, yb, zf), new(xr, yb, zf), new(xl, yb, zb), new(0, -1, 0), u + d + w, v, w, d);
    }
    private void ClearModel() => modelDirty = true;
    private void RequestModelPaint() { if (!FrameDriven) Invalidate(); }
    internal void SetPose(StandingPose pose) { rig.SetPose(pose); ClearModel(); RequestModelPaint(); }
    internal void Animate(Rectangle atmosphereArea, double elapsed)
    {
        rig.Advance(elapsed);
        idleFrameTime += Math.Clamp(elapsed, 0, .05);
        // Slow idle motion needs 60 geometry samples/s; direct camera input still renders at 120 Hz.
        if (Capture || idleFrameTime >= 1d / 60)
        {
            ClearModel();
            idleFrameTime %= 1d / 60;
        }
        if (modelDirty)
        {
            var previousBounds = modelBounds;
            EnsureModel();
            Invalidate(Rectangle.Union(previousBounds, modelBounds));
        }
        if (!atmosphereArea.IsEmpty) Invalidate(atmosphereArea);
        Update();
    }
    protected override void OnSizeChanged(EventArgs e) { ClearModel(); base.OnSizeChanged(e); }
    private void EnsureModel()
    {
        if (Width <= 0 || Height <= 0) return;
        if (model == null || model.Size != ClientSize)
        {
            model?.Dispose();
            model = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            modelDirty = true;
        }
        if (modelDirty)
        {
            using var graphics = Graphics.FromImage(model);
            graphics.Clear(Color.Transparent);
            DrawModel(graphics);
            modelDirty = false;
            ModelFrames++;
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        EnsureModel();
        PaintAtmosphere?.Invoke(e.Graphics, e.ClipRectangle);
        var clip = Rectangle.Intersect(modelBounds, e.ClipRectangle);
        if (model != null && !clip.IsEmpty) e.Graphics.DrawImage(model, clip, clip, GraphicsUnit.Pixel);
    }
    private void DrawModel(Graphics g)
    {
        PixelTheme.Prepare(g);
        var baseScale = Math.Min(Width / 65f, Height / 46f);
        var scale = baseScale * zoom;
        var center = new PointF(Width * HorizontalAnchor, Height * .5f + 16 * scale);
        var elevation = pitch + .19f;
        var sinYaw = MathF.Sin(yaw); var cosYaw = MathF.Cos(yaw);
        var sinElevation = MathF.Sin(elevation); var cosElevation = MathF.Cos(elevation);
        Vector3 Transform(Vector3 p)
        {
            var x = p.X * cosYaw + p.Z * sinYaw;
            var z = -p.X * sinYaw + p.Z * cosYaw;
            return new(x, p.Y * cosElevation - z * sinElevation, p.Y * sinElevation + z * cosElevation);
        }
        PointF Project(Vector3 p) { var q = Transform(p); return new(center.X + q.X * scale, center.Y - q.Y * scale); }
        var phase = System.Diagnostics.Stopwatch.GetTimestamp();
        platformProjection[0] = Project(new(-17, -.3f, -17));
        platformProjection[1] = Project(new(18, -.3f, -17));
        platformProjection[2] = Project(new(-17, -.3f, 18));
        g.DrawImage(platform, platformProjection, new Rectangle(0, 0, 35, 35), GraphicsUnit.Pixel);
        var platformFourth = new PointF(platformProjection[1].X + platformProjection[2].X - platformProjection[0].X,
            platformProjection[1].Y + platformProjection[2].Y - platformProjection[0].Y);
        var platformBounds = Rectangle.FromLTRB(
            (int)MathF.Floor(Math.Min(platformFourth.X, Math.Min(platformProjection[0].X, Math.Min(platformProjection[1].X, platformProjection[2].X)))) - 1,
            (int)MathF.Floor(Math.Min(platformFourth.Y, Math.Min(platformProjection[0].Y, Math.Min(platformProjection[1].Y, platformProjection[2].Y)))) - 1,
            (int)MathF.Ceiling(Math.Max(platformFourth.X, Math.Max(platformProjection[0].X, Math.Max(platformProjection[1].X, platformProjection[2].X)))) + 1,
            (int)MathF.Ceiling(Math.Max(platformFourth.Y, Math.Max(platformProjection[0].Y, Math.Max(platformProjection[1].Y, platformProjection[2].Y)))) + 1);
        modelBounds = Rectangle.Intersect(ClientRectangle, platformBounds);
        PlatformTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phase;
        phase = System.Diagnostics.Stopwatch.GetTimestamp();
        var minX = float.MaxValue; var minY = float.MaxValue;
        var maxX = float.MinValue; var maxY = float.MinValue;
        foreach (var face in faces)
        {
            var bone = rig.Transforms[(int)face.Bone];
            var a = Vector3.Transform(face.A, bone); var b = Vector3.Transform(face.B, bone); var c = Vector3.Transform(face.C, bone);
            face.Depth = Transform((a + b + c) / 3).Z;
            face.Visible = Transform(Vector3.TransformNormal(face.Normal, bone)).Z >= .001f;
            if (!face.Visible) continue;
            face.Projection[0] = Project(a); face.Projection[1] = Project(b); face.Projection[2] = Project(c);
            foreach (var point in face.Projection)
            {
                minX = Math.Min(minX, point.X); minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X); maxY = Math.Max(maxY, point.Y);
            }
            var fourth = Project(b + c - a);
            minX = Math.Min(minX, fourth.X); minY = Math.Min(minY, fourth.Y);
            maxX = Math.Max(maxX, fourth.X); maxY = Math.Max(maxY, fourth.Y);
        }
        if (minX == float.MaxValue) return;
        var radius = Math.Clamp((int)MathF.Round(3 * zoom), 2, 5);
        var spriteBounds = Rectangle.Intersect(Rectangle.FromLTRB((int)MathF.Floor(minX) - radius - 1, (int)MathF.Floor(minY) - radius - 1,
            (int)MathF.Ceiling(maxX) + radius + 1, (int)MathF.Ceiling(maxY) + radius + 1), new Rectangle(-radius, -radius, Width + radius * 2, Height + radius * 2));
        if (spriteBounds.Width <= 0 || spriteBounds.Height <= 0) return;
        modelBounds = Rectangle.Intersect(ClientRectangle, Rectangle.Union(platformBounds, spriteBounds));
        EnsureSpriteBuffers(spriteBounds.Size);
        Array.Sort(orderedFaces, DepthOrder);
        using (var characterGraphics = Graphics.FromImage(characterBuffer!))
        {
            characterGraphics.Clear(Color.Transparent);
            PixelTheme.Prepare(characterGraphics);
            characterGraphics.TranslateTransform(-spriteBounds.X, -spriteBounds.Y);
            foreach (var face in orderedFaces)
            {
                if (!face.Visible) continue;
                characterGraphics.DrawImage(face.Texture, face.Projection, new Rectangle(0, 0, face.Texture.Width, face.Texture.Height), GraphicsUnit.Pixel);
            }
        }
        CharacterTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phase;
        phase = System.Diagnostics.Stopwatch.GetTimestamp();
        Outline(spriteBounds.Size, radius);
        g.DrawImage(outlineBuffer!, spriteBounds, new Rectangle(Point.Empty, spriteBounds.Size), GraphicsUnit.Pixel);
        OutlineTicks += System.Diagnostics.Stopwatch.GetTimestamp() - phase;
    }
    private void EnsureSpriteBuffers(Size size)
    {
        if (characterBuffer != null && characterBuffer.Width >= size.Width && characterBuffer.Height >= size.Height) return;
        var width = Math.Max(characterBuffer?.Width ?? 0, (size.Width + 63) / 64 * 64);
        var height = Math.Max(characterBuffer?.Height ?? 0, (size.Height + 63) / 64 * 64);
        characterBuffer?.Dispose(); outlineBuffer?.Dispose();
        characterBuffer = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        outlineBuffer = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        characterPixels = new byte[width * height * 4]; outlinePixels = new byte[characterPixels.Length];
    }
    private void Outline(Size size, int radius)
    {
        var character = characterBuffer!;
        var bounds = new Rectangle(Point.Empty, character.Size);
        var source = character.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var stride = source.Stride;
        var pixels = characterPixels;
        try { Marshal.Copy(source.Scan0, pixels, 0, pixels.Length); }
        finally { character.UnlockBits(source); }
        var ink = outlinePixels;
        Array.Clear(ink);
        var offsets = OutlineOffsets[radius];
        for (var y = 0; y < size.Height; y++)
        for (var x = 0; x < size.Width; x++)
        {
            var alpha = y * stride + x * 4 + 3;
            if (pixels[alpha] < 64) continue;
            if (x > 0 && x < size.Width - 1 && y > 0 && y < size.Height - 1 &&
                pixels[alpha - 4] >= 64 && pixels[alpha + 4] >= 64 && pixels[alpha - stride] >= 64 && pixels[alpha + stride] >= 64) continue;
            foreach (var offset in offsets)
            {
                var targetX = x + offset.X; var targetY = y + offset.Y;
                if (targetX < 0 || targetX >= size.Width || targetY < 0 || targetY >= size.Height) continue;
                var index = targetY * stride + targetX * 4;
                ink[index] = 5; ink[index + 1] = 8; ink[index + 2] = 5; ink[index + 3] = 255;
            }
        }
        var result = outlineBuffer!;
        var destination = result.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(ink, 0, destination.Scan0, ink.Length); }
        finally { result.UnlockBits(destination); }
        using (var graphics = Graphics.FromImage(result)) graphics.DrawImageUnscaled(character, 0, 0);
    }
    private static Bitmap CreatePlatform()
    {
        var image = new Bitmap(35, 35, PixelFormat.Format32bppPArgb);
        for (var z = -17; z <= 17; z++)
        for (var x = -17; x <= 17; x++)
        {
            var distance = MathF.Sqrt(x * x + z * z) / 17f;
            if (distance > 1) continue;
            var noise = (uint)((x + 73) * 73428767 ^ (z + 91) * 912931);
            if (distance > .80f && noise % 100 > (1 - distance) * 500) continue;
            var alpha = (int)(Math.Clamp((1 - distance) * 2.4f, 0, 1) * 235);
            var shade = (int)(noise % 14) + (x * x + z * z < 17 ? -10 : 0);
            image.SetPixel(x + 17, z + 17, Color.FromArgb(alpha, 43 + shade, 55 + shade, 37 + shade));
        }
        return image;
    }
    internal static Bitmap CreateDefaultSkin()
    {
        using var stream = typeof(SkinScene).Assembly.GetManifestResourceStream("Eclipse.Assets.steve.png")
            ?? throw new InvalidOperationException("Не найдена встроенная текстура Steve.");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var face in faces) face.Texture.Dispose();
            skin.Dispose(); cape?.Dispose(); model?.Dispose(); platform.Dispose(); characterBuffer?.Dispose(); outlineBuffer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
