namespace Eclipse;

internal sealed class SkinsPage : UserControl
{
    private readonly SkinScene scene = new() { Dock = DockStyle.Fill, FrameDriven = true };
    private readonly FlowLayoutPanel skins = new() { Dock = DockStyle.Top, Height = 172, AutoScroll = true, WrapContents = false };
    private readonly FlowLayoutPanel capes = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly PixelButton apply = new() { Text = L.T("Применить"), Primary = true, Dock = DockStyle.Bottom, Height = 52, Enabled = false };
    private readonly Label name = PixelTheme.TextLabel(L.T("Войдите в Microsoft"), 16);
    private readonly Action<string> report;
    private readonly CancellationTokenSource lifetime = new();
    private AppearanceService? service;
    private PlayerTexture? chosenSkin;
    private PlayerTexture? chosenCape;
    private int generation;
    private AppearanceProfile? currentProfile;
    private bool offlineMode;
    private string playerName = "";
    private object? displayedKey;
    public event Func<Task>? ReloadRequested;
    internal void Animate(double elapsed) => scene.Animate(Rectangle.Empty, elapsed);
    public event Func<PlayerTexture?, PlayerTexture?, Task>? ApplyRequested;
    public SkinsPage(Action<string> report)
    {
        this.report = report; BackColor = PixelTheme.Background;
        var right = new Panel { Dock = DockStyle.Right, Width = 388, Padding = new Padding(18, 10, 18, 18), BackColor = PixelTheme.Background };
        var skinLabel = PixelTheme.TextLabel(L.T("МОИ СКИНЫ"), 14); skinLabel.Dock = DockStyle.Top; skinLabel.Height = 32; skinLabel.AutoSize = false;
        var capeLabel = PixelTheme.TextLabel(L.T("МОИ ПЛАЩИ"), 14); capeLabel.Dock = DockStyle.Top; capeLabel.Height = 38; capeLabel.AutoSize = false;
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
        var wardrobe = new Panel { Dock = DockStyle.Top, Height = 400, BackColor = BackColor };
        wardrobe.Controls.Add(capes); wardrobe.Controls.Add(capeLabel); wardrobe.Controls.Add(skins); wardrobe.Controls.Add(skinLabel);
        viewport.Controls.Add(wardrobe);
        var applyArea = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(0, 12, 0, 0), BackColor = BackColor };
        apply.Dock = DockStyle.Fill; applyArea.Controls.Add(apply);
        right.Controls.Add(viewport); right.Controls.Add(applyArea);
        var bar = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(24, 8, 20, 8) };
        name.AutoSize = false; name.Dock = DockStyle.Fill; name.TextAlign = ContentAlignment.MiddleLeft;
        var reload = new PixelButton { Text = L.T("Обновить"), Dock = DockStyle.Right, Width = 145 };
        var import = new PixelButton { Text = L.T("Загрузить скин"), Dock = DockStyle.Right, Width = 175, AccessibleName = L.T("Загрузить скин") };
        import.Click += async (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "PNG (*.png)|*.png", Title = L.T("Загрузить скин"), CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            using var model = new SkinModelDialog();
            if (model.ShowDialog(this) != DialogResult.OK) return;
            try { await ImportFileAsync(picker.FileName, model.Slim); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
            { UiSounds.Play(UiCue.Error); PixelDialog.Message(this, L.T("Скины"), ex.Message); }
        };
        bar.Controls.Add(name); bar.Controls.Add(import); bar.Controls.Add(reload);
        Controls.Add(scene); Controls.Add(right); Controls.Add(bar);
        reload.Click += async (_, _) => { if (ReloadRequested != null) await ReloadRequested(); };
        apply.Click += async (_, _) => { if (ApplyRequested != null) await ApplyRequested(chosenSkin, chosenCape); };
    }
    public async Task SetProfileAsync(AppearanceProfile? profile, AppearanceService service, bool offline = false, string nickname = "", PlayerTexture? focusSkin = null)
    {
        var key = (profile, service, offline, nickname, service.Library.Revision, L.Language);
        if (focusSkin == null && Equals(displayedKey, key)) return;
        displayedKey = null;
        this.service = service;
        currentProfile = profile; offlineMode = offline; playerName = nickname;
        var current = ++generation;
        if (offline) { L.PreserveUserText(name); name.Text = nickname; }
        else if (profile == null) L.SetText(name, "Войдите в Microsoft");
        else { L.PreserveUserText(name); name.Text = profile.Name; }
        foreach (var item in skins.Controls.Cast<Control>().Concat(capes.Controls.Cast<Control>()).ToArray()) item.Dispose();
        skins.Controls.Clear(); capes.Controls.Clear();
        chosenSkin = focusSkin ?? (offline ? service.Library.Selected()?.Texture(true) : profile?.Skins.FirstOrDefault(s => s.Active) ?? profile?.Skins.FirstOrDefault());
        chosenCape = profile?.Capes.FirstOrDefault(s => s.Active);
        apply.Enabled = offline || profile != null;
        if (offline)
        {
            using var steve = SkinScene.CreateDefaultSkin();
            var standard = new TextureTile(null, false, steve, L.T("По умолчанию")) { Selected = chosenSkin == null };
            standard.Click += async (_, _) => { chosenSkin = null; SelectTile(skins, standard); await UpdatePreviewAsync(current); };
            skins.Controls.Add(standard);
        }
        var none = new TextureTile(null, true, null, L.T("Без плаща")) { Selected = chosenCape == null };
        none.Click += async (_, _) => { chosenCape = null; SelectTile(capes, none); await UpdatePreviewAsync(current); };
        capes.Controls.Add(none);
        try
        {
            foreach (var local in service.Library.All())
            {
                using var bitmap = service.Library.Load(local);
                var item = local.Texture(chosenSkin?.Id == "local:" + local.Id);
                var tile = new TextureTile(item, false, bitmap, local.Name) { Selected = item.Id == chosenSkin?.Id };
                tile.Click += async (_, _) => { chosenSkin = item; SelectTile(skins, tile); await UpdatePreviewAsync(current); };
                skins.Controls.Add(tile);
            }
            var number = 0;
            foreach (var item in profile?.Skins ?? [])
            {
                using var bitmap = await service.TextureAsync(item, lifetime.Token);
                if (current != generation || IsDisposed) return;
                var tile = new TextureTile(item, false, bitmap, string.IsNullOrEmpty(item.Alias) ? L.F("Скин {0:00}", ++number) : item.Alias) { Selected = item == chosenSkin };
                tile.Click += async (_, _) => { chosenSkin = item; SelectTile(skins, tile); await UpdatePreviewAsync(current); };
                skins.Controls.Add(tile);
            }
            foreach (var item in profile?.Capes ?? [])
            {
                using var bitmap = await service.TextureAsync(item, lifetime.Token);
                if (current != generation || IsDisposed) return;
                var tile = new TextureTile(item, true, bitmap, string.IsNullOrEmpty(item.Alias) ? L.T("Плащ") : item.Alias) { Selected = item == chosenCape };
                tile.Click += async (_, _) => { chosenCape = item; SelectTile(capes, tile); await UpdatePreviewAsync(current); };
                capes.Controls.Add(tile);
            }
            await UpdatePreviewAsync(current);
            if (current == generation && !IsDisposed && focusSkin == null) displayedKey = key;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException) { report(L.T("Не удалось загрузить текстуру: ") + ex.Message); }
    }
    internal async Task ImportFileAsync(string path, bool slim)
    {
        if (service == null) return;
        var local = await Task.Run(() => service.Library.Import(path, slim), lifetime.Token);
        await SetProfileAsync(currentProfile, service, offlineMode, playerName, local.Texture(false));
    }
    private static void SelectTile(FlowLayoutPanel panel, TextureTile tile)
    {
        foreach (var item in panel.Controls.OfType<TextureTile>()) { item.Selected = item == tile; item.Invalidate(); }
    }
    private async Task UpdatePreviewAsync(int current)
    {
        if (service == null) return;
        try
        {
            using var skin = chosenSkin == null ? null : await service.TextureAsync(chosenSkin, lifetime.Token);
            using var cape = chosenCape == null ? null : await service.TextureAsync(chosenCape, lifetime.Token);
            if (current == generation && !IsDisposed) scene.SetAppearance(skin, chosenSkin?.Variant == "SLIM", cape);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException) { report(ex.Message); }
    }
    protected override void Dispose(bool disposing) { if (disposing) { lifetime.Cancel(); lifetime.Dispose(); } base.Dispose(disposing); }
}

internal sealed class TextureTile : Control
{
    private readonly Bitmap? texture;
    private readonly bool cape;
    private readonly string label;
    [System.ComponentModel.DefaultValue(false)] internal bool Selected { get; set; }
    public TextureTile(PlayerTexture? item, bool cape, Bitmap? image, string label)
    {
        this.cape = cape; this.label = label; texture = image == null ? null : new Bitmap(image);
        Size = new Size(102, 144); Margin = new Padding(0, 0, 10, 10); Cursor = Cursors.Hand; DoubleBuffered = true; AccessibleName = label; Tag = item;
    }
    protected override void OnClick(EventArgs e) { UiSounds.Play(UiCue.Select); base.OnClick(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; PixelTheme.Prepare(g);
        PixelTheme.Box(g, ClientRectangle, Selected ? Color.FromArgb(49, 60, 41) : PixelTheme.Panel);
        if (Selected) { using var pen = new Pen(PixelTheme.Accent, 2); g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); }
        if (texture == null) PixelTheme.Label(g, "—", new Rectangle(0, 14, Width, 85), 28, PixelTheme.Muted, StringAlignment.Center);
        else if (cape) g.DrawImage(texture, new Rectangle(29, 16, 40, 80), new Rectangle(1, 1, 10, 16), GraphicsUnit.Pixel);
        else
        {
            g.DrawImage(texture, new Rectangle(39, 15, 24, 24), new Rectangle(8, 8, 8, 8), GraphicsUnit.Pixel);
            g.DrawImage(texture, new Rectangle(39, 39, 24, 36), new Rectangle(20, 20, 8, 12), GraphicsUnit.Pixel);
            g.DrawImage(texture, new Rectangle(27, 39, 12, 36), new Rectangle(44, 20, 4, 12), GraphicsUnit.Pixel);
            g.DrawImage(texture, new Rectangle(63, 39, 12, 36), new Rectangle(44, 20, 4, 12), GraphicsUnit.Pixel);
            g.DrawImage(texture, new Rectangle(39, 75, 12, 36), new Rectangle(4, 20, 4, 12), GraphicsUnit.Pixel);
            g.DrawImage(texture, new Rectangle(51, 75, 12, 36), new Rectangle(4, 20, 4, 12), GraphicsUnit.Pixel);
        }
        PixelTheme.Label(g, label, new Rectangle(4, Height - 31, Width - 8, 26), 10, Selected ? PixelTheme.Text : PixelTheme.Muted, StringAlignment.Center);
    }
    protected override void Dispose(bool disposing) { if (disposing) texture?.Dispose(); base.Dispose(disposing); }
}
