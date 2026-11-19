using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;

namespace Eclipse;

public sealed class LauncherConfig
{
    public string MicrosoftClientId { get; set; } = "";
}

internal sealed class MainForm : Form
{
    private readonly AppPaths paths;
    private readonly bool smoke;
    private readonly LauncherBackdrop backdrop = new();
    private readonly LauncherConfig? configurationOverride;
    internal TabCarousel Navigation { get; } = new();
    internal bool Ready { get; private set; }
    internal PlaytimeBadge HoursBadge => playtimeBadge;
    private readonly Panel pages = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
    private readonly Panel home = new() { Dock = DockStyle.Fill };
    private readonly SkinScene scene = new() { Dock = DockStyle.Fill };
    private readonly PixelComboBox versions = new() { Width = 300, AccessibleName = L.T("Версия Minecraft") };
    private readonly PixelButton installVersion = new() { Text = "+", Width = 38, Height = 36, AccessibleName = L.T("Установить версию") };
    private readonly PixelButton profileActions = new() { Text = "...", Width = 38, Height = 36, AccessibleName = L.T("Профили") };
    private readonly PixelButton login = new() { Text = L.T("Войти"), Width = 165, Quiet = true, AccessibleName = L.T("Войти через Microsoft") };
    private readonly PixelButton logout = new() { Text = L.T("Выйти"), Width = 100, Quiet = true, Visible = false };
    private readonly PixelButton play = new() { Text = L.T("ИГРАТЬ"), Width = 360, Height = 64, Primary = true, Font = PixelTheme.Font(24), AccessibleName = L.T("Играть") };
    private readonly PlaytimeBadge playtimeBadge = new();
    private readonly System.Windows.Forms.Timer playtimeTimer = new() { Interval = 1000 };
    private readonly PixelButton update = new() { Text = L.T("Обновить"), Width = 142, Primary = true, Visible = false, Font = PixelTheme.Font(14), AccessibleName = L.T("Обновить лаунчер") };
    private readonly LauncherUpdates? updatesOverride;
    private LauncherUpdates? updates;
    private LauncherRelease? availableUpdate;
    private bool closingForUpdate;
    private readonly PixelButton cancel = new() { Text = L.T("Отмена"), Width = 110, Dock = DockStyle.Right, Quiet = true, Visible = false };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Cursor = Cursors.Hand };
    private readonly ToolTip tip = new() { AutoPopDelay = 12000 };
    private HttpClient? http;
    private Authentication? auth;
    private Minecraft? minecraft;
    private AppearanceService? appearance;
    private AppearanceProfile? profile;
    private GalleryPage? gallery;
    private GameFilesPage? files;
    private SkinsPage? skins;
    private SettingsPage? options;
    private Settings settings = new();
    private CancellationTokenSource? operation;
    private readonly CancellationTokenSource lifetime = new();
    private IProgress<string>? currentProgress;
    private string? errorDetails;
    private volatile bool running;
    private int appearanceGeneration;
    private bool bindingVersions;
    private string selectedVersion = "";

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    public MainForm(AppPaths paths, bool smoke, LauncherConfig? configurationOverride = null, LauncherUpdates? updatesOverride = null)
    {
        this.paths = paths; this.smoke = smoke; this.configurationOverride = configurationOverride; this.updatesOverride = updatesOverride;
        Text = "Eclipse — Minecraft Java"; Font = PixelTheme.Font(); ForeColor = PixelTheme.Text; BackColor = PixelTheme.Background;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("Eclipse.Assets.Eclipse.ico")!) Icon = new Icon(icon);
        FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(1280, 850); MinimumSize = new Size(1040, 720);
        StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true; KeyPreview = true;
        Padding = new Padding(1);
        pages.BackColor = home.BackColor = PixelTheme.Background;
        var launchBar = new Panel { Dock = DockStyle.Bottom, Height = 173, BackColor = PixelTheme.Background };
        launchBar.Controls.Add(play); launchBar.Controls.Add(versions); launchBar.Controls.Add(installVersion); launchBar.Controls.Add(profileActions);
        play.BringToFront();
        void LayoutLaunch()
        {
            play.Location = new Point((launchBar.Width - play.Width) / 2, 8);
            playtimeBadge.Bounds = new Rectangle((launchBar.Width - 190) / 2, play.Bottom - 12, 190, 48);
            versions.Location = new Point((launchBar.Width - versions.Width - installVersion.Width - profileActions.Width - 16) / 2, 119);
            installVersion.Location = new Point(versions.Right + 8, 119);
            profileActions.Location = new Point(installVersion.Right + 8, 119);
        }
        launchBar.Resize += (_, _) => LayoutLaunch(); LayoutLaunch();
        home.Controls.Add(scene); home.Controls.Add(launchBar); pages.Controls.Add(home);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(26, 2, 20, 5), BackColor = PixelTheme.Background };
        status.Font = PixelTheme.Font(11); status.ForeColor = PixelTheme.Muted;
        footer.Controls.Add(status); footer.Controls.Add(cancel);
        var chrome = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(16, 5, 6, 4), BackColor = PixelTheme.Header };
        var brand = new BrandMark { Dock = DockStyle.Left, Width = 185 };
        var close = new PixelButton { Text = "×", Width = 46, Dock = DockStyle.Right, Quiet = true, AccessibleName = L.T("Закрыть") };
        var maximize = new PixelButton { Text = "□", Width = 46, Dock = DockStyle.Right, Quiet = true, AccessibleName = L.T("Развернуть") };
        var minimize = new PixelButton { Text = "_", Width = 46, Dock = DockStyle.Right, Quiet = true, AccessibleName = L.T("Свернуть") };
        login.Dock = logout.Dock = update.Dock = DockStyle.Right;
        foreach (var button in new[] { login, logout, update, minimize, maximize, close }) button.BackColor = PixelTheme.Header;
        cancel.BackColor = PixelTheme.Background;
        chrome.Controls.Add(brand); chrome.Controls.Add(update); chrome.Controls.Add(logout); chrome.Controls.Add(login); chrome.Controls.Add(minimize); chrome.Controls.Add(maximize); chrome.Controls.Add(close);
        void Drag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } }
        chrome.MouseDown += Drag; brand.MouseDown += Drag;
        chrome.DoubleClick += (_, _) => ToggleMaximize();
        close.Click += (_, _) => Close(); maximize.Click += (_, _) => ToggleMaximize(); minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        Controls.Add(pages); Controls.Add(footer); Controls.Add(Navigation); Controls.Add(chrome);
        Navigation.Dock = DockStyle.Top;
        Navigation.PaintBackdrop = (graphics, clip) => PaintBackdrop(graphics, Navigation, clip);
        scene.FrameDriven = true;
        scene.HorizontalAnchor = .5f;
        scene.PaintAtmosphere = (graphics, clip) =>
        {
            PaintBackdrop(graphics, scene, clip);
            PaintAtmosphere(graphics, scene, clip);
        };
        pages.Paint += (_, e) =>
        {
            PaintBackdrop(e.Graphics, pages, e.ClipRectangle);
            PaintAtmosphere(e.Graphics, pages, e.ClipRectangle, includeParticles: false);
        };
        launchBar.Paint += (_, e) =>
        {
            PaintBackdrop(e.Graphics, launchBar, e.ClipRectangle);
            // Paint the plaque on the parent, so the native Play button always covers it.
            playtimeBadge.Draw(e.Graphics);
        };
        footer.Paint += (_, e) => PaintBackdrop(e.Graphics, footer, e.ClipRectangle);
        Resize += (_, _) => { launchBar.Invalidate(); footer.Invalidate(); pages.Invalidate(); };
        status.BackColor = Color.Transparent;
        Navigation.AtmosphereChanged += elapsed =>
        {
            if (scene.Visible) RefreshAtmosphere(scene, elapsed);
            if (skins?.Visible == true) skins.Animate(elapsed);
        };
        Navigation.Activated += async tab => await OpenTabAsync(tab);
        login.Click += async (_, _) =>
        {
            if (settings.OfflineMode) Navigation.Open(LauncherTab.Settings);
            else await RunAsync(SignInAsync);
        };
        logout.Click += (_, _) =>
        {
            auth?.SignOut(); profile = null; login.Text = L.T("Войти"); logout.Visible = false; scene.SetAppearance(null, false, null);
            if (skins != null && appearance != null) _ = skins.SetProfileAsync(null, appearance);
            SetStatus(L.T("Вы вышли из аккаунта"));
        };
        play.Click += async (_, _) => await RunAsync(PlayAsync);
        update.Click += async (_, _) => await RunAsync(UpdateLauncherAsync);
        playtimeTimer.Tick += (_, _) => RefreshPlaytime();
        installVersion.Click += async (_, _) => await InstallVersionAsync();
        profileActions.Click += async (_, _) => await EditProfileAsync();
        versions.SelectedIndexChanged += (_, _) => ChangeVersion();
        cancel.Click += (_, _) => operation?.Cancel();
        status.Click += (_, _) => { if (errorDetails != null) PixelDialog.Message(this, "Eclipse", errorDetails); };
        Shown += async (_, _) =>
        {
            Refresh();
            if (smoke)
            {
                JsonStore.Write(paths.FilePath("startup.json"), new { firstPaintMs = Program.Startup.Elapsed.TotalMilliseconds, processId = Environment.ProcessId, workingSetBytes = Environment.WorkingSet });
                BeginInvoke(Close); return;
            }
            await InitializeAsync();
        };
        FormClosing += (_, e) =>
        {
            if (running) { e.Cancel = true; WindowState = FormWindowState.Minimized; return; }
            if (!closingForUpdate && files != null && !files.ConfirmLeave()) { e.Cancel = true; return; }
            operation?.Cancel(); lifetime.Cancel();
        };
        FormClosed += (_, _) => { playtimeTimer.Dispose(); http?.Dispose(); tip.Dispose(); backdrop.Dispose(); };
        L.CaptureTree(this);
    }
    private void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84 && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            var point = PointToClient(new Point(unchecked((short)m.LParam.ToInt64()), unchecked((short)(m.LParam.ToInt64() >> 16))));
            var left = point.X < 6; var right = point.X >= Width - 6; var top = point.Y < 6; var bottom = point.Y >= Height - 6;
            var code = top && left ? 13 : top && right ? 14 : bottom && left ? 16 : bottom && right ? 17 : left ? 10 : right ? 11 : top ? 12 : bottom ? 15 : 0;
            if (code != 0) m.Result = (IntPtr)code;
            return;
        }
        base.WndProc(ref m);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var input = FindFocused(this);
        if (input is TextBoxBase or ComboBox { DroppedDown: true } or NumericUpDown || input?.Parent is NumericUpDown)
            return base.ProcessCmdKey(ref msg, keyData);
        if (input == update && keyData is Keys.Space or Keys.Enter) { if (update.Enabled) update.PerformClick(); return true; }
        if (input == profileActions && keyData is Keys.Space or Keys.Enter) { if (profileActions.Enabled) profileActions.PerformClick(); return true; }
        if (keyData == Keys.Space && Navigation.ActiveTab == LauncherTab.Eclipse && input is not PixelComboBox && input != installVersion && !versions.DroppedDown)
        {
            if (play.Enabled) play.PerformClick();
            return true;
        }
        if (keyData == Keys.Enter && input == installVersion) { if (installVersion.Enabled) installVersion.PerformClick(); return true; }
        if (keyData == Keys.A) { Navigation.MoveSelection(-1); return true; }
        if (keyData == Keys.D) { Navigation.MoveSelection(1); return true; }
        if (keyData == Keys.Enter) { Navigation.Commit(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private static Control? FindFocused(Control parent)
    {
        foreach (Control child in parent.Controls) { if (child.Focused) return child; if (child.ContainsFocus) return FindFocused(child); }
        return null;
    }
    private void PaintBackdrop(Graphics graphics, Control target, Rectangle clip)
    {
        var origin = PointToClient(target.PointToScreen(Point.Empty));
        var fadeTop = PointToClient(play.PointToScreen(Point.Empty)).Y;
        backdrop.Draw(graphics, origin, clip, ClientSize, fadeTop);
    }
    private void PaintAtmosphere(Graphics graphics, Control target, Rectangle clip, bool includeParticles = true)
    {
        var origin = Navigation.PointToClient(target.PointToScreen(Point.Empty));
        Navigation.DrawAtmosphere(graphics, origin, clip, includeParticles);
    }
    private void RefreshAtmosphere(Control target, double elapsed)
    {
        if (!target.IsHandleCreated || !target.Visible) return;
        var origin = Navigation.PointToClient(target.PointToScreen(Point.Empty));
        var area = Rectangle.Intersect(target.ClientRectangle, new Rectangle(-origin.X, HeaderParticles.EffectTop - origin.Y,
            Navigation.Width, HeaderParticles.BandHeight));
        if (target is SkinScene skinScene) { skinScene.Animate(area, elapsed); return; }
        if (area.IsEmpty) return;
        target.Invalidate(area); target.Update();
    }
    private async Task OpenTabAsync(LauncherTab tab)
    {
        if (IsDisposed) return;
        try
        {
            Control page;
            switch (tab)
            {
                case LauncherTab.Screenshots: gallery ??= new GalleryPage(paths, SetStatus); page = gallery; break;
                case LauncherTab.Files: files ??= new GameFilesPage(paths, SetStatus); page = files; break;
                case LauncherTab.Skins:
                    if (skins == null)
                    {
                        skins = new SkinsPage(SetStatus);
                        skins.ReloadRequested += () => RunAsync(async ct =>
                        {
                            if (settings.OfflineMode) { await RefreshSkinsAsync(); return; }
                            var account = await AuthenticateAsync(false, ct); await LoadAppearanceAsync(appearance!.Remember(account));
                        });
                        skins.ApplyRequested += (skin, cape) => RunAsync(async ct =>
                        {
                            if (settings.OfflineMode)
                            {
                                var local = appearance!.Library.Find(skin);
                                if (skin != null && local == null) throw new InvalidOperationException(L.T("Скин отсутствует в библиотеке."));
                                appearance.Library.Select(local);
                                await LoadLocalAppearanceAsync(); await RefreshSkinsAsync();
                                SetStatus(L.T("Внешний вид сохранён")); return;
                            }
                            var account = await AuthenticateAsync(false, ct);
                            var updated = await appearance!.ApplyAsync(account, skin, cape, ct);
                            await LoadAppearanceAsync(updated); SetStatus(L.T("Внешний вид сохранён"));
                        });
                    }
                    page = skins; break;
                case LauncherTab.Settings:
                    if (options == null)
                    {
                        options = new SettingsPage(); options.Bind(settings);
                        options.LanguageChanged += language =>
                        {
                            try
                            {
                                settings.Language = language;
                                JsonStore.Write(paths.FilePath("settings.json"), settings);
                                ApplyLanguage(language);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); }
                        };
                        options.SaveRequested += () =>
                        {
                            try
                            {
                                var previousMode = settings.OfflineMode;
                                options.ReadTo(settings); JsonStore.Write(paths.FilePath("settings.json"), settings);
                                RefreshIdentity();
                                if (previousMode && !settings.OfflineMode && appearance?.CachedProfile() is { } cached && cached.Name == auth?.SavedName)
                                    _ = LoadAppearanceAsync(cached);
                                SetStatus(L.T("Настройки сохранены"));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ShowError(ex); }
                        };
                        options.RefreshVersionsRequested += async () => await RunAsync(ct => LoadVersionsAsync(true, ct));
                    }
                    page = options; break;
                default: page = home; break;
            }
            if (!pages.Controls.Contains(page)) { L.CaptureTree(page); page.Dock = DockStyle.Fill; pages.Controls.Add(page); }
            foreach (Control item in pages.Controls) item.Visible = item == page;
            page.BringToFront();
            if (tab == LauncherTab.Screenshots) await gallery!.ReloadAsync();
            if (tab == LauncherTab.Files && files!.Tag is not true) { files.Tag = true; await files.ReloadAsync(); }
            if (tab == LauncherTab.Skins && appearance != null) await RefreshSkinsAsync();
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { ShowError(ex); }
    }
    private async Task InitializeAsync()
    {
        SetBusy(true);
        try
        {
            var config = await Task.Run(() =>
            {
                settings = JsonStore.Read<Settings>(paths.FilePath("settings.json")) ?? new();
                return configurationOverride ?? JsonStore.Read<LauncherConfig>(Path.Combine(AppContext.BaseDirectory, "launcher-config.json")) ?? new();
            }, lifetime.Token);
            UiSounds.Enabled = settings.InterfaceSounds;
            http = Network.Create(); auth = new Authentication(http, paths, config.MicrosoftClientId); minecraft = new Minecraft(http, paths); appearance = new AppearanceService(http, paths);
            minecraft.GameStarted += () =>
            {
                running = true;
                try
                {
                    if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() =>
                    {
                        if (IsDisposed) return;
                        cancel.Visible = false;
                        if (settings.MinimizeOnLaunch) WindowState = FormWindowState.Minimized;
                    }));
                }
                catch (InvalidOperationException) { /* Closing the UI must not interrupt process tracking. */ }
            };
            ApplyLanguage(settings.Language);
            updates = updatesOverride ?? new LauncherUpdates(http);
            RefreshPlaytime(); playtimeTimer.Start();
            _ = CheckUpdatesAsync();
            var name = settings.OfflineMode ? null : await Task.Run(() => auth.SavedName, lifetime.Token);
            RefreshIdentity(name);
            options?.Bind(settings);
            await LoadVersionsAsync(false, lifetime.Token);
            SetStatus(settings.OfflineMode || !string.IsNullOrEmpty(name) ? L.T("Готов к запуску") : L.T("Войдите в Microsoft"));
            var cached = settings.OfflineMode ? null : await Task.Run(() => appearance.CachedProfile(), lifetime.Token);
            // Cosmetic downloads must never hold up the Play button.
            Ready = true;
            SetBusy(false);
            if (!settings.OfflineMode && !string.IsNullOrEmpty(name) && cached?.Name == name) await LoadAppearanceAsync(cached);
            if (skins != null) await RefreshSkinsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
        finally { if (!Ready) { Ready = true; if (!IsDisposed) SetBusy(false); } }
    }
    private async Task LoadAppearanceAsync(AppearanceProfile updated)
    {
        if (appearance == null || settings.OfflineMode) return;
        profile = updated;
        var generation = ++appearanceGeneration;
        var skin = updated.Skins.FirstOrDefault(s => s.Active);
        var cape = updated.Capes.FirstOrDefault(s => s.Active);
        try
        {
            using var skinBitmap = skin == null ? null : await appearance.TextureAsync(skin, lifetime.Token);
            using var capeBitmap = cape == null ? null : await appearance.TextureAsync(cape, lifetime.Token);
            if (IsDisposed || generation != appearanceGeneration) return;
            scene.SetAppearance(skinBitmap, skin?.Variant == "SLIM", capeBitmap);
            if (skins != null) await RefreshSkinsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException) { SetStatus(L.T("Скин временно недоступен")); }
    }
    private async Task SignInAsync(CancellationToken ct)
    {
        var account = await AuthenticateAsync(true, ct);
        _ = LoadAppearanceAsync(appearance!.Remember(account)); SetStatus(L.T("Лицензия подтверждена"));
    }
    private Task RefreshSkinsAsync() => skins != null && appearance != null
        ? skins.SetProfileAsync(profile, appearance, settings.OfflineMode, settings.Nickname) : Task.CompletedTask;
    private async Task LoadLocalAppearanceAsync()
    {
        if (appearance == null) return;
        var generation = ++appearanceGeneration;
        try
        {
            var local = appearance.Library.Selected();
            using var bitmap = local == null ? null : await Task.Run(() => appearance.Library.Load(local), lifetime.Token);
            if (!IsDisposed && settings.OfflineMode && generation == appearanceGeneration)
                scene.SetAppearance(bitmap, local?.Variant == "SLIM", null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ArgumentException) { if (!IsDisposed) ShowError(ex); }
    }
    private async Task<LicensedAccount> AuthenticateAsync(bool interactive, CancellationToken ct)
    {
        if (settings.OfflineMode) throw new InvalidOperationException(L.T("Для скинов и плащей выберите вход Microsoft в настройках"));
        var progress = currentProgress!;
        var account = await Task.Run(() => auth!.AuthenticateAsync(interactive, progress, ct), ct);
        if (!settings.OfflineMode) { login.Text = account.Name; logout.Visible = true; }
        return account;
    }
    private async Task PlayAsync(CancellationToken ct)
    {
        var version = VersionId(versions.SelectedItem);
        if (string.IsNullOrEmpty(version)) throw new InvalidOperationException(L.T("Выберите версию Minecraft"));
        settings.Version = version; JsonStore.Write(paths.FilePath("settings.json"), settings);
        var progress = currentProgress!;
        var offline = settings.OfflineMode ? OfflineAccount.Create(settings.Nickname) : null;
        var account = offline == null ? await AuthenticateAsync(false, ct) : null;
        if (account != null) _ = LoadAppearanceAsync(appearance!.Remember(account));
        var plan = await Task.Run(() => minecraft!.PrepareAsync(version, progress, ct), ct);
        var exitCode = await Task.Run(() => offline != null
            ? minecraft!.LaunchOfflineAsync(plan, offline, settings.MemoryMb, progress, ct, settings)
            : minecraft!.LaunchAsync(plan, account!, settings.MemoryMb, progress, ct, settings), ct);
        RefreshPlaytime();
        if (exitCode == 0) SetStatus(L.T("Minecraft закрыт"));
        else { SetStatus(L.F("Minecraft завершился с кодом {0}", exitCode)); errorDetails = L.T("Журнал: ") + paths.FilePath("logs/game-latest.log"); }
    }
    private void RefreshPlaytime()
    {
        if (playtimeBadge.SetHours(minecraft?.Playtime.TotalHours ?? 0)) play.Parent?.Invalidate(playtimeBadge.Bounds);
        if (play.Parent != null) play.Parent.AccessibleDescription = playtimeBadge.AccessibleName;
    }
    private void ApplyLanguage(string language)
    {
        L.SetLanguage(language);
        L.RefreshTree(this);
        Navigation.RefreshLanguage();
        options?.RefreshLanguage();
        RefreshIdentity();
        RefreshPlaytime();
        play.Text = operation != null ? L.T("ПОДГОТОВКА…") : L.T("ИГРАТЬ");
        if (operation == null) update.Text = L.T("Обновить");
        if (Ready && operation == null) SetStatus(L.T("Готов к запуску"));
        if (skins?.Visible == true && appearance != null) _ = RefreshSkinsAsync();
    }
    private async Task CheckUpdatesAsync()
    {
        try
        {
            var release = await updates!.CheckAsync(lifetime.Token);
            if (IsDisposed || lifetime.IsCancellationRequested || release == null) return;
            availableUpdate = release;
            update.Visible = true; update.Enabled = Ready && operation == null;
            tip.SetToolTip(update, "Eclipse " + release.Version.ToString(3));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException or ArgumentException)
        { Debug.WriteLine("Update check: " + ex.Message); }
    }
    private async Task UpdateLauncherAsync(CancellationToken ct)
    {
        if (updates == null || availableUpdate == null) return;
        try
        {
            SetStatus(L.T("Загрузка обновления Eclipse…"));
            var progress = new Progress<int>(percent => { if (!IsDisposed) update.Text = percent + "%"; });
            var directory = paths.FilePath("updates/" + availableUpdate.Version.ToString(3) + "-" + Guid.NewGuid().ToString("N"));
            var installer = await updates.DownloadAsync(availableUpdate, directory, progress, ct);
            ct.ThrowIfCancellationRequested();
            if (files != null && !files.ConfirmLeave()) return;
            var info = LauncherUpdates.InstallerStartInfo(installer, AppContext.BaseDirectory, paths.Root, Environment.ProcessId);
            using var process = Process.Start(info) ?? throw new InvalidOperationException(L.T("Не удалось запустить обновление."));
            closingForUpdate = true;
            Close();
        }
        finally { closingForUpdate = false; if (!IsDisposed) update.Text = L.T("Обновить"); }
    }
    private void RefreshIdentity(string? savedName = null)
    {
        if (settings.OfflineMode)
        {
            login.Text = OfflineAccount.Create(settings.Nickname).Name;
            login.AccessibleName = L.T("Настройки никнейма");
            logout.Visible = false;
            appearanceGeneration++;
            profile = null;
            if (appearance != null) _ = LoadLocalAppearanceAsync();
            if (skins != null && appearance != null) _ = RefreshSkinsAsync();
        }
        else
        {
            var name = savedName ?? auth?.SavedName;
            login.Text = string.IsNullOrEmpty(name) ? L.T("Войти") : name;
            login.AccessibleName = L.T("Войти через Microsoft");
            logout.Visible = !string.IsNullOrEmpty(name);
        }
    }
    private async Task LoadVersionsAsync(bool refreshList, CancellationToken ct)
    {
        if (minecraft == null) return;
        SetStatus(L.T("Загрузка версий…"));
        var previous = VersionId(versions.SelectedItem);
        if (string.IsNullOrEmpty(previous)) previous = settings.Version;
        var manifest = await Task.Run(() => minecraft.VersionsAsync(refreshList, ct), ct);
        var list = manifest.GetProperty("versions").EnumerateArray().Where(v => v.Text("type") == "release" && SupportedRelease(v.Text("id"))).Select(v => v.Text("id")).ToArray();
        bindingVersions = true;
        versions.BeginUpdate(); versions.Items.Clear();
        var savedProfiles = ModdedProfile.ReadAll(paths);
        versions.Items.AddRange(savedProfiles.Cast<object>());
        versions.Items.AddRange(list.Where(id => !savedProfiles.Any(p => p.Id == id)).ToArray());
        versions.SelectedItem = versions.Items.FirstOrDefault(item => VersionId(item) == previous) ?? manifest.GetProperty("latest").Text("release");
        if (versions.SelectedIndex < 0 && list.Length > 0) versions.SelectedIndex = 0;
        versions.EndUpdate(); bindingVersions = false; ChangeVersion(); SetStatus(L.T("Версии обновлены"));
    }
    private static string VersionId(object? item) => item switch { ModdedProfile profile => profile.Id, string id => id, _ => "" };
    private void ChangeVersion()
    {
        if (bindingVersions) return;
        var next = VersionId(versions.SelectedItem);
        if (next == selectedVersion || string.IsNullOrEmpty(next)) return;
        if (files != null && !files.ConfirmLeave())
        {
            bindingVersions = true;
            versions.SelectedItem = versions.Items.FirstOrDefault(item => VersionId(item) == selectedVersion);
            bindingVersions = false; return;
        }
        paths.UseGameDirectory(versions.SelectedItem is ModdedProfile profile ? profile.GameDirectory : "");
        if (gallery != null) { pages.Controls.Remove(gallery); gallery.Dispose(); gallery = null; }
        if (files != null) { pages.Controls.Remove(files); files.Dispose(); files = null; }
        selectedVersion = next; settings.Version = next;
        if (Ready)
        {
            try { JsonStore.Write(paths.FilePath("settings.json"), settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); }
        }
    }
    private async Task InstallVersionAsync()
    {
        if (operation != null || minecraft == null || http == null) return;
        var baseVersion = versions.SelectedItem is ModdedProfile profile ? profile.GameVersion : VersionId(versions.SelectedItem);
        var games = versions.Items.Select(item => item is ModdedProfile p ? p.GameVersion : VersionId(item)).Distinct().ToArray();
        using var dialog = new VersionInstallerDialog(new ModLoaders(http, paths, minecraft), games, baseVersion);
        dialog.ShowDialog(this);
        if (dialog.InstalledProfile is not { } installed || IsDisposed) return;
        try
        {
            settings.Version = installed.Id;
            await LoadVersionsAsync(false, lifetime.Token);
            versions.SelectedItem = versions.Items.OfType<ModdedProfile>().First(p => p.Id == installed.Id);
            JsonStore.Write(paths.FilePath("settings.json"), settings);
            SetStatus(L.T("Установлено: ") + installed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
    }
    private async Task EditProfileAsync()
    {
        if (operation != null || minecraft == null) return;
        var selected = versions.SelectedItem;
        var id = VersionId(selected);
        if (string.IsNullOrEmpty(id)) return;
        using var dialog = new ProfileEditDialog(selected!.ToString()!);
        var result = dialog.ShowDialog(this);
        if (result is not (DialogResult.OK or DialogResult.Yes)) return;
        if (result == DialogResult.OK && files != null && !files.ConfirmLeave()) return;
        await RunAsync(async ct =>
        {
            var manager = new GameProfiles(paths);
            var changed = result == DialogResult.Yes ? manager.Rename(id, dialog.ProfileName)
                : await Task.Run(() => manager.CopyAsync(id, dialog.ProfileName, dialog.CopyContent, dialog.CopyWorlds, currentProgress!, ct), ct);
            settings.Version = changed.Id;
            await LoadVersionsAsync(false, ct);
            versions.SelectedItem = versions.Items.OfType<ModdedProfile>().First(p => p.Id == changed.Id);
            JsonStore.Write(paths.FilePath("settings.json"), settings);
            SetStatus(L.T("Профиль сохранён"));
        });
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation != null || auth == null || minecraft == null) return;
        var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = pending; SetBusy(true);
        try { await action(pending.Token); }
        catch (OperationCanceledException) { if (!IsDisposed) SetStatus(L.T("Операция отменена или истекло время ожидания")); }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
        finally
        {
            running = false; operation = null; pending.Dispose();
            if (!IsDisposed && !lifetime.IsCancellationRequested) { SetBusy(false); if (WindowState == FormWindowState.Minimized && settings.MinimizeOnLaunch) WindowState = FormWindowState.Normal; }
        }
    }
    private void SetBusy(bool busy)
    {
        installVersion.Enabled = !busy;
        profileActions.Enabled = !busy;
        update.Enabled = !busy && availableUpdate != null;
        login.Enabled = logout.Enabled = play.Enabled = versions.Enabled = !busy;
        play.Text = busy ? L.T("ПОДГОТОВКА…") : L.T("ИГРАТЬ");
        cancel.Visible = busy && operation != null && !running;
        if (busy) currentProgress = new Progress<string>(message =>
        {
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            SetStatus(message);
        });
    }
    private void SetStatus(string message)
    {
        if (IsDisposed) return;
        errorDetails = null; status.ForeColor = PixelTheme.Muted; status.Text = message; tip.SetToolTip(status, message);
    }
    private void ShowError(Exception ex)
    {
        UiSounds.Play(UiCue.Error);
        var message = ex is HttpRequestException ? L.T("Нет соединения с сервером") : ex.Message;
        if (ex is OAuthConfigurationException) message = L.T("Нужно настроить вход Microsoft — нажмите для инструкции");
        if (ex is MinecraftAppApprovalException) message = L.T("Minecraft не одобрил приложение — нажмите для инструкции");
        if (message.Contains("MicrosoftClientId", StringComparison.Ordinal)) message = L.T("Вход Microsoft ещё не настроен");
        SetStatus(message); status.ForeColor = Color.FromArgb(218, 160, 130); errorDetails = ex.Message; tip.SetToolTip(status, errorDetails);
        if (ex is OAuthConfigurationException) PixelDialog.Message(this, L.T("Настройка Microsoft"), ex.Message);
        if (ex is MinecraftAppApprovalException) PixelDialog.Message(this, L.T("Разрешение Minecraft API"), ex.Message);
    }
    private static bool SupportedRelease(string version) => !version.StartsWith("1.", StringComparison.Ordinal) || (Version.TryParse(version, out var parsed) && parsed.Minor >= 18);
}

internal sealed class BrandMark : Control
{
    public BrandMark() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        PixelTheme.Prepare(e.Graphics);
        PixelTheme.Icon(e.Graphics, 2, new Rectangle(0, 4, 32, 32), PixelTheme.Accent);
        PixelTheme.Label(e.Graphics, "ECLIPSE", new Rectangle(44, 0, Width - 44, Height), 17, PixelTheme.Text);
    }
}
