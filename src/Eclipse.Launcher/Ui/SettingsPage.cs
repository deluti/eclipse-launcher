namespace Eclipse;

internal sealed class SettingsPage : UserControl
{
    private readonly TextBox memory = new() { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, AutoSize = false, Width = 104, Height = 28 };
    private readonly PixelSlider slider = new();
    private readonly CheckBox automatic = new() { Text = L.T("Авто"), AutoSize = true, FlatStyle = FlatStyle.Flat, AccessibleName = L.T("Автоматическая память") };
    private int manualMemory = 4096;
    private int javaMajor = 21;
    private readonly AppPaths? paths;
    private readonly TextBox nickname = new() { BorderStyle = BorderStyle.None, AutoSize = false, MaxLength = 16, AccessibleName = L.T("Никнейм") };
    private readonly PixelComboBox loginMode = new() { Width = 338, AccessibleName = L.T("Способ входа") };
    private readonly PixelComboBox resolution = new() { Width = 230 };
    private readonly PixelComboBox language = new() { Width = 260, AccessibleName = L.T("Язык") };
    private bool binding;
    public event Action<string>? LanguageChanged;
    private readonly CheckBox fullscreen = new() { Text = L.T("Полный экран"), AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox minimize = new() { Text = L.T("Свернуть лаунчер при запуске"), AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox sounds = new() { Text = L.T("Звуки интерфейса"), AutoSize = true, FlatStyle = FlatStyle.Flat };
    public event Action? SaveRequested;
    public event Action? RefreshVersionsRequested;
    public SettingsPage(AppPaths? paths = null)
    {
        this.paths = paths;
        BackColor = PixelTheme.Background;
        var panel = new Panel { Width = 680, Height = 450, BackColor = PixelTheme.Background, Anchor = AnchorStyles.Top };
        var title = PixelTheme.TextLabel(L.T("НАСТРОЙКИ ЗАПУСКА"), 22); title.Location = new Point(0, 12);
        var nicknameTitle = PixelTheme.TextLabel(L.T("НИКНЕЙМ"), 14); nicknameTitle.Location = new Point(0, 48);
        var modeTitle = PixelTheme.TextLabel(L.T("ВХОД"), 14); modeTitle.Location = new Point(340, 48);
        PixelTheme.Style(nickname); nickname.Bounds = new Rectangle(0, 72, 300, 34);
        loginMode.Location = new Point(340, 72); loginMode.Items.AddRange([L.T("По никнейму"), "Microsoft"]);
        loginMode.SelectedIndexChanged += (_, _) => nickname.Enabled = loginMode.SelectedIndex == 0;
        nickname.KeyPress += (_, _) => UiSounds.Play(UiCue.Tick);
        var memoryTitle = PixelTheme.TextLabel(L.T("ПАМЯТЬ MINECRAFT"), 14); memoryTitle.Location = new Point(0, 126);
        PixelTheme.Style(automatic); automatic.Location = new Point(318, 121);
        PixelTheme.Style(memory); memory.Location = new Point(494, 118); memory.AccessibleName = L.T("Память Minecraft, МБ");
        var less = new PixelButton { Text = "-", Bounds = new Rectangle(456, 116, 32, 32) };
        var more = new PixelButton { Text = "+", Bounds = new Rectangle(604, 116, 32, 32) };
        void Step(int delta) { var current = int.TryParse(memory.Text, out var parsed) ? parsed : 4096; memory.Text = Math.Clamp(current + delta, 1024, 32768).ToString(); }
        less.Click += (_, _) => Step(-512); more.Click += (_, _) => Step(512);
        var units = PixelTheme.TextLabel(L.T("МБ"), 12); units.Location = new Point(642, 128);
        automatic.CheckedChanged += (_, _) =>
        {
            if (!binding)
            {
                if (automatic.Checked && int.TryParse(memory.Text, out var amount)) manualMemory = amount;
                memory.Text = (automatic.Checked ? RecommendedMemory() : manualMemory).ToString();
                UiSounds.Play(UiCue.Select);
            }
            memory.Enabled = slider.Enabled = less.Enabled = more.Enabled = !automatic.Checked;
        };
        slider.Bounds = new Rectangle(0, 162, 678, 30);
        var low = PixelTheme.TextLabel(L.T("1 ГБ"), 12); low.ForeColor = PixelTheme.Muted; low.Location = new Point(0, 198);
        var high = PixelTheme.TextLabel(L.T("32 ГБ"), 12); high.ForeColor = PixelTheme.Muted; high.Location = new Point(628, 198);
        var sizeTitle = PixelTheme.TextLabel(L.T("РАЗМЕР ОКНА"), 14); sizeTitle.Location = new Point(0, 232);
        resolution.Location = new Point(0, 258);
        resolution.Items.AddRange(["854 × 480", "1280 × 720", "1600 × 900", "1920 × 1080", "2560 × 1440"]);
        PixelTheme.Style(fullscreen); fullscreen.Location = new Point(288, 266); fullscreen.BackColor = BackColor;
        PixelTheme.Style(minimize); minimize.Location = new Point(0, 312); minimize.BackColor = BackColor;
        PixelTheme.Style(sounds); sounds.Location = new Point(390, 312); sounds.BackColor = BackColor;
        sounds.CheckedChanged += (_, _) => UiSounds.Enabled = sounds.Checked;
        foreach (var option in new[] { fullscreen, minimize, sounds }) option.Click += (_, _) => UiSounds.Play(UiCue.Select);
        memory.KeyPress += (_, _) => UiSounds.Play(UiCue.Tick);
        var languageTitle = PixelTheme.TextLabel(L.T("ЯЗЫК"), 14); languageTitle.Location = new Point(0, 360);
        language.Location = new Point(200, 350);
        language.Items.AddRange(["English", "Русский"]);
        language.SelectedIndexChanged += (_, _) => { if (!binding) LanguageChanged?.Invoke(language.SelectedIndex == 1 ? "ru" : "en"); };
        var save = new PixelButton { Text = L.T("Сохранить"), Primary = true, Bounds = new Rectangle(0, 406, 250, 42) };
        var refresh = new PixelButton { Text = L.T("Обновить версии"), Bounds = new Rectangle(290, 406, 270, 42) };
        var graphics = new PixelButton { Text = "GPU", AccessibleName = L.T("Проверка GPU"), Bounds = new Rectangle(580, 406, 100, 42) };
        graphics.Click += async (_, _) =>
        {
            graphics.Enabled = false;
            try
            {
                var saved = paths == null ? null : JsonStore.Read<Settings>(paths.FilePath("settings.json"));
                var plan = saved == null || string.IsNullOrEmpty(saved.Version) ? null : JsonStore.Read<InstallPlan>(AppPaths.SafeChild(paths!.Root, "plans/" + saved.Version + ".json"));
                var java = plan == null ? null : AppPaths.SafeChild(paths!.Root, plan.JavaRelativePath);
                var report = await Task.Run(() => WindowsGraphics.Read(java));
                if (IsDisposed) return;
                var lines = report.Adapters.Select(a => a.Name + " · " + a.DedicatedBytes / 1048576 + " MB");
                PixelDialog.Message(this, "GPU", string.Join("\n", lines) + "\n\n" + L.T("Выбор GPU: ") + report.JavaPreference + "\n" + java + "\n\n" + L.T("В Windows → Система → Дисплей → Графика можно выбрать GPU для этого javaw.exe. Системные настройки не изменялись."));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException) { if (!IsDisposed) PixelDialog.Message(this, "GPU", ex.Message); }
            finally { if (!IsDisposed) graphics.Enabled = true; }
        };
        save.Click += (_, _) => SaveRequested?.Invoke();
        refresh.Click += (_, _) => RefreshVersionsRequested?.Invoke();
        slider.Changed += value => memory.Text = value.ToString();
        memory.TextChanged += (_, _) => { if (int.TryParse(memory.Text, out var parsed)) slider.SetValue(parsed); };
        panel.Controls.AddRange([title, nicknameTitle, nickname, modeTitle, loginMode, memoryTitle, automatic, memory, less, more, units, slider, low, high, sizeTitle, resolution, fullscreen, minimize, sounds, languageTitle, language, save, refresh, graphics]);
        Controls.Add(panel);
        Resize += (_, _) => panel.Location = new Point(Math.Max(24, (Width - panel.Width) / 2), Math.Max(6, (Height - panel.Height) / 3));
        L.CaptureTree(this);
    }
    public void Bind(Settings settings)
    {
        binding = true;
        try
        {
            if (paths != null && !string.IsNullOrEmpty(settings.Version) && JsonStore.Read<InstallPlan>(AppPaths.SafeChild(paths.Root, "plans/" + settings.Version + ".json")) is { } plan)
                javaMajor = JavaRuntimeInfo.RequiredMajor(plan);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
        nickname.Text = settings.Nickname;
        loginMode.SelectedIndex = settings.OfflineMode ? 0 : 1;
        nickname.Enabled = settings.OfflineMode;
        memory.Text = Math.Clamp(settings.MemoryMb, 1024, 32768).ToString();
        manualMemory = Math.Clamp(settings.MemoryMb, 1024, 32768);
        automatic.Checked = settings.AutomaticMemory;
        if (automatic.Checked) memory.Text = RecommendedMemory().ToString();
        var text = $"{settings.WindowWidth} × {settings.WindowHeight}";
        if (!resolution.Items.Contains(text)) resolution.Items.Add(text);
        resolution.SelectedItem = text;
        fullscreen.Checked = settings.Fullscreen;
        minimize.Checked = settings.MinimizeOnLaunch;
        sounds.Checked = settings.InterfaceSounds;
        language.SelectedIndex = settings.Language == "ru" ? 1 : 0;
        binding = false;
    }
    internal void RefreshLanguage()
    {
        loginMode.Items[0] = L.T("По никнейму");
        loginMode.Invalidate();
        L.RefreshTree(this);
    }
    public void ReadTo(Settings settings)
    {
        var offline = loginMode.SelectedIndex == 0;
        var name = offline ? OfflineAccount.Create(nickname.Text).Name : nickname.Text.Trim();
        if (!int.TryParse(memory.Text, out var memoryMb) || memoryMb is < 1024 or > 32768) throw new InvalidOperationException(L.T("Введите память от 1024 до 32768 МБ"));
        settings.MemoryMb = automatic.Checked ? manualMemory : memoryMb;
        settings.AutomaticMemory = automatic.Checked;
        if (resolution.SelectedItem is string text)
        {
            var parts = text.Split('×');
            settings.WindowWidth = Math.Clamp(int.Parse(parts[0].Trim()), 640, 7680);
            settings.WindowHeight = Math.Clamp(int.Parse(parts[1].Trim()), 360, 4320);
        }
        settings.Fullscreen = fullscreen.Checked;
        settings.MinimizeOnLaunch = minimize.Checked;
        settings.InterfaceSounds = sounds.Checked;
        settings.Nickname = name;
        settings.OfflineMode = offline;
        settings.Language = language.SelectedIndex == 1 ? "ru" : "en";
    }
    private int RecommendedMemory()
    {
        var mods = paths == null ? 0 : MemoryBudget.CountMods(paths.Game);
        return MemoryBudget.Calculate(MemoryStatus.Read(), true, manualMemory, javaMajor, mods).MaximumMb;
    }
}

internal sealed class PixelSlider : Control
{
    private int value = 4096;
    public event Action<int>? Changed;
    public PixelSlider()
    {
        DoubleBuffered = true; Cursor = Cursors.Hand; TabStop = true; AccessibleName = L.T("Выделенная память");
        MouseDown += (_, e) => { Capture = true; MoveTo(e.X); Focus(); };
        MouseMove += (_, e) => { if (Capture) MoveTo(e.X); };
        MouseUp += (_, _) => Capture = false;
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right) { ChangeFromInput(value + (e.KeyCode == Keys.Right ? 512 : -512)); e.Handled = true; } };
    }
    public void SetValue(int newValue) { value = Math.Clamp(newValue, 1024, 32768); Invalidate(); }
    private void MoveTo(int x)
    {
        ChangeFromInput(1024 + (int)Math.Round(Math.Clamp((x - 8f) / Math.Max(1, Width - 16), 0, 1) * (32768 - 1024) / 512f) * 512);
    }
    private void ChangeFromInput(int next)
    {
        var previous = value; SetValue(next);
        if (previous == value) return;
        UiSounds.Play(UiCue.Tick); Changed?.Invoke(value);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var x = 8 + (Width - 16) * (value - 1024) / (32768 - 1024);
        using var track = new SolidBrush(PixelTheme.Surface); g.FillRectangle(track, 4, Height / 2 - 4, Width - 8, 8);
        using var fill = new SolidBrush(Color.FromArgb(89, 123, 65)); g.FillRectangle(fill, 4, Height / 2 - 4, x, 8);
        PixelTheme.Box(g, new Rectangle(x - 8, 0, 16, Height), PixelTheme.Accent);
    }
}
