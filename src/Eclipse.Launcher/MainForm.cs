using System.Runtime.InteropServices;
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
    private readonly Panel pages = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 0) };
    private readonly Panel home = new() { Dock = DockStyle.Fill };
    private readonly SkinScene scene = new() { Dock = DockStyle.Fill };
    private readonly PixelComboBox versions = new() { Width = 300, AccessibleName = "Версия Minecraft" };
    private readonly PixelButton installVersion = new() { Text = "+", Width = 38, Height = 36, AccessibleName = "Установить версию" };
    private readonly PixelButton login = new() { Text = "Войти", Width = 165, Quiet = true, AccessibleName = "Войти через Microsoft" };
    private readonly PixelButton logout = new() { Text = "Выйти", Width = 100, Quiet = true, Visible = false };
    private readonly PixelButton play = new() { Text = "ИГРАТЬ", Width = 360, Height = 64, Primary = true, Font = PixelTheme.Font(24), AccessibleName = "Играть" };
    private readonly PixelButton cancel = new() { Text = "Отмена", Width = 110, Dock = DockStyle.Right, Quiet = true, Visible = false };
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
    private bool running;
    private int appearanceGeneration;
    private bool bindingVersions;
    private string selectedVersion = "";

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    public MainForm(AppPaths paths, bool smoke, LauncherConfig? configurationOverride = null)
    {
        this.paths = paths; this.smoke = smoke; this.configurationOverride = configurationOverride;
        Text = "Eclipse — Minecraft Java"; Font = PixelTheme.Font(); ForeColor = PixelTheme.Text; BackColor = PixelTheme.Background;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("Eclipse.Assets.Eclipse.ico")!) Icon = new Icon(icon);
        FormBorderStyle = FormBorderStyle.None; ClientSize = new Size(1280, 850); MinimumSize = new Size(1040, 720);
        StartPosition = FormStartPosition.CenterScreen; DoubleBuffered = true; KeyPreview = true;
        Padding = new Padding(1);
        pages.BackColor = home.BackColor = PixelTheme.Background;
        var launchBar = new Panel { Dock = DockStyle.Bottom, Height = 139, BackColor = PixelTheme.Background };
        launchBar.Controls.Add(play); launchBar.Controls.Add(versions); launchBar.Controls.Add(installVersion);
        void LayoutLaunch()
        {
            play.Location = new Point((launchBar.Width - play.Width) / 2, 8);
            versions.Location = new Point((launchBar.Width - versions.Width - installVersion.Width - 8) / 2, 86);
            installVersion.Location = new Point(versions.Right + 8, 86);
        }
        launchBar.Resize += (_, _) => LayoutLaunch(); LayoutLaunch();
        home.Controls.Add(scene); home.Controls.Add(launchBar); pages.Controls.Add(home);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(26, 2, 20, 5), BackColor = PixelTheme.Background };
        status.Font = PixelTheme.Font(11); status.ForeColor = PixelTheme.Muted;
        footer.Controls.Add(status); footer.Controls.Add(cancel);
        var chrome = new Panel { Dock = DockStyle.Top, Height = 50, Padding = new Padding(16, 5, 6, 4), BackColor = PixelTheme.Header };
        var brand = new BrandMark { Dock = DockStyle.Left, Width = 185 };
        var close = new PixelButton { Text = "×", Width = 46, Dock = DockStyle.Right, Quiet = true, AccessibleName = "Закрыть" };
        var maximize = new PixelButton { Text = "□", Width = 46, Dock = DockStyle.Right, Quiet = true, AccessibleName = "Развернуть" };
        var minimize = new PixelButton { Text = "_", Width = 46, Dock = DockStyle.Right, Quiet = true, AccessibleName = "Свернуть" };
        login.Dock = logout.Dock = DockStyle.Right;
        foreach (var button in new[] { login, logout, minimize, maximize, close }) button.BackColor = PixelTheme.Header;
        cancel.BackColor = PixelTheme.Background;
        chrome.Controls.Add(brand); chrome.Controls.Add(logout); chrome.Controls.Add(login); chrome.Controls.Add(minimize); chrome.Controls.Add(maximize); chrome.Controls.Add(close);
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
        launchBar.Paint += (_, e) => PaintBackdrop(e.Graphics, launchBar, e.ClipRectangle);
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
            auth?.SignOut(); profile = null; login.Text = "Войти"; logout.Visible = false; scene.SetAppearance(null, false, null);
            if (skins != null && appearance != null) _ = skins.SetProfileAsync(null, appearance);
            SetStatus("Вы вышли из аккаунта");
        };
        play.Click += async (_, _) => await RunAsync(PlayAsync);
        installVersion.Click += async (_, _) => await InstallVersionAsync();
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
            if (files != null && !files.ConfirmLeave()) { e.Cancel = true; return; }
            operation?.Cancel(); lifetime.Cancel();
        };
        FormClosed += (_, _) => { http?.Dispose(); tip.Dispose(); backdrop.Dispose(); };
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
                        skins.ReloadRequested += () => RunAsync(async ct => { var account = await AuthenticateAsync(false, ct); await LoadAppearanceAsync(appearance!.Remember(account)); });
                        skins.ApplyRequested += (skin, cape) => RunAsync(async ct =>
                        {
                            var account = await AuthenticateAsync(false, ct);
                            var updated = await appearance!.ApplyAsync(account, skin, cape, ct);
                            await LoadAppearanceAsync(updated); SetStatus("Внешний вид сохранён");
                        });
                    }
                    page = skins; break;
                case LauncherTab.Settings:
                    if (options == null)
                    {
                        options = new SettingsPage(); options.Bind(settings);
                        options.SaveRequested += () =>
                        {
                            try
                            {
                                var previousMode = settings.OfflineMode;
                                options.ReadTo(settings); JsonStore.Write(paths.FilePath("settings.json"), settings);
                                RefreshIdentity();
                                if (previousMode && !settings.OfflineMode && appearance?.CachedProfile() is { } cached && cached.Name == auth?.SavedName)
                                    _ = LoadAppearanceAsync(cached);
                                SetStatus("Настройки сохранены");
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ShowError(ex); }
                        };
                        options.RefreshVersionsRequested += async () => await RunAsync(ct => LoadVersionsAsync(true, ct));
                    }
                    page = options; break;
                default: page = home; break;
            }
            if (!pages.Controls.Contains(page)) { page.Dock = DockStyle.Fill; pages.Controls.Add(page); }
            foreach (Control item in pages.Controls) item.Visible = item == page;
            page.BringToFront();
            if (tab == LauncherTab.Screenshots) await gallery!.ReloadAsync();
            if (tab == LauncherTab.Files && files!.Tag is not true) { files.Tag = true; await files.ReloadAsync(); }
            if (tab == LauncherTab.Skins && appearance != null) await skins!.SetProfileAsync(profile, appearance);
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
            var name = settings.OfflineMode ? null : await Task.Run(() => auth.SavedName, lifetime.Token);
            RefreshIdentity(name);
            options?.Bind(settings);
            await LoadVersionsAsync(false, lifetime.Token);
            SetStatus(settings.OfflineMode || !string.IsNullOrEmpty(name) ? "Готов к запуску" : "Войдите в Microsoft");
            var cached = settings.OfflineMode ? null : await Task.Run(() => appearance.CachedProfile(), lifetime.Token);
            // Cosmetic downloads must never hold up the Play button.
            Ready = true;
            SetBusy(false);
            if (!settings.OfflineMode && !string.IsNullOrEmpty(name) && cached?.Name == name) await LoadAppearanceAsync(cached);
            if (skins != null) await skins.SetProfileAsync(profile, appearance);
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
            if (skins != null) await skins.SetProfileAsync(profile, appearance);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException) { SetStatus("Скин временно недоступен"); }
    }
    private async Task SignInAsync(CancellationToken ct)
    {
        var account = await AuthenticateAsync(true, ct);
        _ = LoadAppearanceAsync(appearance!.Remember(account)); SetStatus("Лицензия подтверждена");
    }
    private async Task<LicensedAccount> AuthenticateAsync(bool interactive, CancellationToken ct)
    {
        if (settings.OfflineMode) throw new InvalidOperationException("Для скинов и плащей выберите вход Microsoft в настройках");
        var progress = currentProgress!;
        var account = await Task.Run(() => auth!.AuthenticateAsync(interactive, progress, ct), ct);
        if (!settings.OfflineMode) { login.Text = account.Name; logout.Visible = true; }
        return account;
    }
    private async Task PlayAsync(CancellationToken ct)
    {
        var version = VersionId(versions.SelectedItem);
        if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("Выберите версию Minecraft");
        settings.Version = version; JsonStore.Write(paths.FilePath("settings.json"), settings);
        var progress = currentProgress!;
        var offline = settings.OfflineMode ? OfflineAccount.Create(settings.Nickname) : null;
        var account = offline == null ? await AuthenticateAsync(false, ct) : null;
        if (account != null) _ = LoadAppearanceAsync(appearance!.Remember(account));
        var plan = await Task.Run(() => minecraft!.PrepareAsync(version, progress, ct), ct);
        var exitCode = await Task.Run(() => offline != null
            ? minecraft!.LaunchOfflineAsync(plan, offline, settings.MemoryMb, progress, ct, settings)
            : minecraft!.LaunchAsync(plan, account!, settings.MemoryMb, progress, ct, settings), ct);
        if (exitCode == 0) SetStatus("Minecraft закрыт");
        else { SetStatus($"Minecraft завершился с кодом {exitCode}"); errorDetails = "Журнал: " + paths.FilePath("logs/game-latest.log"); }
    }
    private void RefreshIdentity(string? savedName = null)
    {
        if (settings.OfflineMode)
        {
            login.Text = OfflineAccount.Create(settings.Nickname).Name;
            login.AccessibleName = "Настройки никнейма";
            logout.Visible = false;
            appearanceGeneration++;
            if (profile != null) { profile = null; scene.SetAppearance(null, false, null); }
            if (skins != null && appearance != null) _ = skins.SetProfileAsync(null, appearance);
        }
        else
        {
            var name = savedName ?? auth?.SavedName;
            login.Text = string.IsNullOrEmpty(name) ? "Войти" : name;
            login.AccessibleName = "Войти через Microsoft";
            logout.Visible = !string.IsNullOrEmpty(name);
        }
    }
    private async Task LoadVersionsAsync(bool refreshList, CancellationToken ct)
    {
        if (minecraft == null) return;
        SetStatus("Загрузка версий…");
        var previous = VersionId(versions.SelectedItem);
        if (string.IsNullOrEmpty(previous)) previous = settings.Version;
        var manifest = await Task.Run(() => minecraft.VersionsAsync(refreshList, ct), ct);
        var list = manifest.GetProperty("versions").EnumerateArray().Where(v => v.Text("type") == "release" && SupportedRelease(v.Text("id"))).Select(v => v.Text("id")).ToArray();
        bindingVersions = true;
        versions.BeginUpdate(); versions.Items.Clear();
        versions.Items.AddRange(ModdedProfile.ReadAll(paths).Cast<object>());
        versions.Items.AddRange(list);
        versions.SelectedItem = versions.Items.FirstOrDefault(item => VersionId(item) == previous) ?? manifest.GetProperty("latest").Text("release");
        if (versions.SelectedIndex < 0 && list.Length > 0) versions.SelectedIndex = 0;
        versions.EndUpdate(); bindingVersions = false; ChangeVersion(); SetStatus("Версии обновлены");
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
        using var dialog = new VersionInstallerDialog(new ModLoaders(http, paths, minecraft), versions.Items.OfType<string>(), baseVersion);
        dialog.ShowDialog(this);
        if (dialog.InstalledProfile is not { } installed || IsDisposed) return;
        try
        {
            settings.Version = installed.Id;
            await LoadVersionsAsync(false, lifetime.Token);
            versions.SelectedItem = versions.Items.OfType<ModdedProfile>().First(p => p.Id == installed.Id);
            JsonStore.Write(paths.FilePath("settings.json"), settings);
            SetStatus("Установлено: " + installed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation != null || auth == null || minecraft == null) return;
        var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation = pending; SetBusy(true);
        try { await action(pending.Token); }
        catch (OperationCanceledException) { if (!IsDisposed) SetStatus("Операция отменена или истекло время ожидания"); }
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
        login.Enabled = logout.Enabled = play.Enabled = versions.Enabled = !busy;
        play.Text = busy ? "ПОДГОТОВКА…" : "ИГРАТЬ";
        cancel.Visible = busy && operation != null && !running;
        if (busy) currentProgress = new Progress<string>(message =>
        {
            if (IsDisposed || lifetime.IsCancellationRequested) return;
            SetStatus(message);
            if (message.StartsWith("Minecraft запущен.", StringComparison.Ordinal))
            { running = true; cancel.Visible = false; if (settings.MinimizeOnLaunch) WindowState = FormWindowState.Minimized; }
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
        var message = ex is HttpRequestException ? "Нет соединения с сервером" : ex.Message;
        if (ex is OAuthConfigurationException) message = "Нужно настроить вход Microsoft — нажмите для инструкции";
        if (ex is MinecraftAppApprovalException) message = "Minecraft не одобрил приложение — нажмите для инструкции";
        if (message.Contains("MicrosoftClientId", StringComparison.Ordinal)) message = "Вход Microsoft ещё не настроен";
        SetStatus(message); status.ForeColor = Color.FromArgb(218, 160, 130); errorDetails = ex.Message; tip.SetToolTip(status, errorDetails);
        if (ex is OAuthConfigurationException) PixelDialog.Message(this, "Настройка Microsoft", ex.Message);
        if (ex is MinecraftAppApprovalException) PixelDialog.Message(this, "Разрешение Minecraft API", ex.Message);
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
