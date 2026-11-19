using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using Eclipse;

internal static class UiTests
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetGlyphIndices(IntPtr dc, string text, int count, ushort[] glyphs, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    public static int Run(string root, string screenshotDirectory, string language = "ru")
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                L.SetLanguage("en");
                var paths = new AppPaths(Path.Combine(root, "ui", Guid.NewGuid().ToString("N")));
                JsonStore.Write(paths.FilePath("settings.json"), new Settings { Language = language });
                Directory.CreateDirectory(paths.Game);
                File.WriteAllText(Path.Combine(paths.Game, "options.txt"), "lang:ru_ru\r\nrenderDistance:12\r\n");
                Directory.CreateDirectory(Path.Combine(paths.Game, "screenshots"));
                using (var image = new Bitmap(640, 360))
                {
                    using var g = Graphics.FromImage(image);
                    g.Clear(Color.FromArgb(107, 160, 190));
                    using var ground = new SolidBrush(Color.FromArgb(74, 111, 48)); g.FillRectangle(ground, 0, 220, 640, 140);
                    image.Save(Path.Combine(paths.Game, "screenshots", "fixture.png"), ImageFormat.Png);
                }
                JsonStore.Write(paths.FilePath("versions.json"), new { latest = new { release = "26.3" }, versions = new[] { new { id = "26.3", type = "release" }, new { id = "1.21.1", type = "release" } } });
                JsonStore.Write(paths.FilePath("profiles.json"), new[] { new ModdedProfile("fabric-loader-1.2.3-26.3", "26.3", "fabric", "1.2.3") });
                JsonStore.Write(paths.FilePath("playtime.json"), new { TotalSeconds = 4444.4 });
                Directory.CreateDirectory(screenshotDirectory);
                using var updateHandler = new UpdateHandler();
                using var updateHttp = new HttpClient(updateHandler);
                using var form = new MainForm(paths, false, new LauncherConfig(), new LauncherUpdates(updateHttp));
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        for (var i = 0; !form.Ready && i < 200; i++) await Task.Delay(25);
                        Check(form.Ready, "Window initialization");
                        Check(L.Language == language, "The saved language is restored from disk after a fresh English startup");
                        await CheckUpdateAndHoursAsync(form, updateHandler);
                        await CheckLanguageSwitchAsync(form, paths, language);
                        await CheckAnimationAsync(form, screenshotDirectory);
                        await CheckRotationAsync(form, screenshotDirectory);
                        CheckSoundFeedback();
                        CheckSpaceLaunch(form);
                        await CheckIdlePosesAsync(form, screenshotDirectory);
                        CheckCapeViews(form, screenshotDirectory);
                        await CheckInstallerAsync(form, screenshotDirectory);
                        ButtonPaintingTests.Run();
                        Check(form.Navigation.ActiveTab == LauncherTab.Eclipse, "Eclipse opens by default");
                        Check(PixelTheme.Font().FontFamily.Name.Contains("Monocraft"), "Embedded pixel font");
                        CheckCyrillic(form);
                        CheckHeader(form);
                        await Task.Delay(900);
                        CheckEffectPlacement(form);
                        using (var steve = SkinScene.CreateDefaultSkin())
                        {
                            Check(steve.Width == 64 && steve.Height == 64, "The bundled Steve texture is 64x64");
                            using var stream = typeof(SkinScene).Assembly.GetManifestResourceStream("Eclipse.Assets.steve.png")!;
                            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
                            Check(hash == "06628F9CEF88520DA742AB280A01677CE17C99717F964ED89ED791673121094C", "The fallback is Mojang's original Steve texture");
                        }
                        Check(!Find<Label>(form).Any(l => l.Text.Contains("ОТКРЫТЬ") || l.Text.Contains("ВЫБОР")), "No bottom-right keyboard hint remains");
                        Capture(form, screenshotDirectory, "eclipse");
                        var versionSelector = Find<PixelComboBox>(form).Single(c => c.AccessibleName == L.T("Версия Minecraft"));
                        typeof(PixelComboBox).GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(versionSelector, null);
                        Check(versionSelector.DroppedDown, "Pixel version dropdown opens");
                        var popup = (ToolStripDropDown)typeof(PixelComboBox).GetField("popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(versionSelector)!;
                        var choices = (ListBox)((ToolStripControlHost)popup.Items[0]).Control;
                        choices.SelectedIndex = versionSelector.Items.IndexOf("1.21.1");
                        typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(choices, [new KeyEventArgs(Keys.Enter)]);
                        Check(!versionSelector.DroppedDown && versionSelector.SelectedItem?.ToString() == "1.21.1", "Pixel dropdown selects a version");
                        versionSelector.SelectedItem = "26.3";
                        versionSelector.SelectedItem = versionSelector.Items.OfType<ModdedProfile>().Single();
                        Check(paths.Game == Path.Combine(paths.Root, "instances", "fabric-loader-1.2.3-26.3"), "Selecting Fabric switches to its isolated game folder");
                        Check(JsonStore.Read<Settings>(paths.FilePath("settings.json"))!.Version == "fabric-loader-1.2.3-26.3", "The selected loader profile persists without starting the game");
                        Capture(form, screenshotDirectory, "fabric-selected");
                        versionSelector.SelectedItem = "26.3";
                        Check(paths.Game == paths.SharedGame, "Selecting vanilla restores its game folder");
                        var scene = Find<SkinScene>(form).Single();
                        var before = Snapshot(scene);
                        typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0)]);
                        typeof(Control).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 0, 170, 110, 0)]);
                        typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 1, 170, 110, 0)]);
                        Check(!before.SequenceEqual(Snapshot(scene)), "Dragging rotates the rendered skin");
                        var rotated = Snapshot(scene);
                        typeof(Control).GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, [new MouseEventArgs(MouseButtons.None, 0, 100, 100, 120)]);
                        Check(!rotated.SequenceEqual(Snapshot(scene)), "Wheel zooms the rendered skin");
                        typeof(Control).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 2, 100, 100, 0)]);
                        Command(form, Keys.D);
                        Check(form.Navigation.FocusedTab == LauncherTab.Skins && form.Navigation.ActiveTab == LauncherTab.Eclipse, "D changes focus, not the page");
                        Command(form, Keys.Enter); await Task.Delay(250);
                        Check(Find<SkinsPage>(form).Single().Visible, "Enter opens skins");
                        Capture(form, screenshotDirectory, "skins");
                        await CheckSkinLibraryAsync(form, paths, screenshotDirectory);
                        Command(form, Keys.D); Command(form, Keys.Enter); await Task.Delay(250);
                        var settingsPage = Find<SettingsPage>(form).Single();
                        Check(settingsPage.Visible && form.Navigation.ActiveTab == LauncherTab.Settings, "Fifth tab is settings");
                        var memory = Find<TextBox>(settingsPage).Single(t => t.AccessibleName == L.T("Память Minecraft, МБ")); memory.Text = "6144";
                        var nickname = Find<TextBox>(settingsPage).Single(t => t.AccessibleName == L.T("Никнейм"));
                        nickname.Focus();
                        Check(!Command(form, Keys.A), "Typing A in nickname does not switch tabs");
                        nickname.Text = "EclipsePlayer";
                        var loginMode = Find<PixelComboBox>(settingsPage).Single(c => c.AccessibleName == L.T("Способ входа"));
                        Check(loginMode.SelectedIndex == 0 && nickname.Enabled, "Nickname login is available without Microsoft configuration");
                        Find<PixelButton>(settingsPage).Single(b => b.Text == L.T("Сохранить")).PerformClick();
                        Check(JsonStore.Read<Settings>(paths.FilePath("settings.json"))!.MemoryMb == 6144, "Memory setting persists");
                        var savedIdentity = JsonStore.Read<Settings>(paths.FilePath("settings.json"))!;
                        Check(savedIdentity.OfflineMode && savedIdentity.Nickname == "EclipsePlayer", "Nickname and local mode persist");
                        Check(Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Настройки никнейма")).Text == "EclipsePlayer", "Header uses the saved nickname");
                        var sounds = Find<CheckBox>(settingsPage).Single(c => c.Text == L.T("Звуки интерфейса"));
                        Check(sounds.Checked, "Interface sounds default on");
                        sounds.Checked = false;
                        Check(!UiSounds.Enabled, "Sound toggle mutes immediately");
                        Find<PixelButton>(settingsPage).Single(b => b.Text == L.T("Сохранить")).PerformClick();
                        Check(!JsonStore.Read<Settings>(paths.FilePath("settings.json"))!.InterfaceSounds, "Mute setting persists");
                        sounds.Checked = true;
                        Find<PixelButton>(settingsPage).Single(b => b.Text == L.T("Сохранить")).PerformClick();
                        form.Navigation.Focus();
                        Capture(form, screenshotDirectory, "settings");
                        Command(form, Keys.D); Command(form, Keys.Enter); await Task.Delay(350);
                        var gallery = Find<GalleryPage>(form).Single();
                        Check(gallery.Visible && Find<ScreenshotTile>(gallery).Count() == 1, "Gallery loads screenshots");
                        Capture(form, screenshotDirectory, "screenshots");
                        Command(form, Keys.D); Command(form, Keys.Enter); await Task.Delay(350);
                        var files = Find<GameFilesPage>(form).Single();
                        var tree = Find<TreeView>(files).Single();
                        Check(tree.Nodes.Count == 1, "File tree loads");
                        var optionsNode = tree.Nodes[0].Nodes.Cast<TreeNode>().Single(n => n.Tag is string p && p.EndsWith("options.txt"));
                        tree.SelectedNode = optionsNode; await Task.Delay(200);
                        var editor = Find<TextBox>(files).Single();
                        Check(editor.Text.Contains("renderDistance:12"), "File text loads");
                        editor.Focus();
                        Check(!Command(form, Keys.A), "Typing A in editor does not switch tabs");
                        editor.Text = editor.Text.Replace("renderDistance:12", "renderDistance:18");
                        Find<PixelButton>(files).Single(b => b.Text == L.T("Сохранить")).PerformClick();
                        Check(File.ReadAllText(Path.Combine(paths.Game, "options.txt")).Contains("renderDistance:18"), "File changes save");
                        Capture(form, screenshotDirectory, "files");
                        form.Navigation.Focus(); Command(form, Keys.D); Command(form, Keys.Enter); await Task.Delay(250);
                        var nicknameButton = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Настройки никнейма"));
                        nicknameButton.PerformClick(); await Task.Delay(100);
                        Check(form.Navigation.ActiveTab == LauncherTab.Settings, "Nickname button opens settings");
                        loginMode.SelectedIndex = 1;
                        Check(!nickname.Enabled, "Nickname is disabled for a Microsoft account");
                        Find<PixelButton>(settingsPage).Single(b => b.Text == L.T("Сохранить")).PerformClick();
                        var login = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Войти через Microsoft")); login.PerformClick(); await Task.Delay(150);
                        Check(Find<Label>(form).Any(l => l.Text == L.T("Вход Microsoft ещё не настроен")), "Unconfigured login fails visibly");
                        loginMode.SelectedIndex = 0;
                        Find<PixelButton>(settingsPage).Single(b => b.Text == L.T("Сохранить")).PerformClick();
                        form.Size = new Size(1040, 720); await Task.Delay(100); Capture(form, screenshotDirectory, "compact");
                        foreach (var size in new[] { new Size(1040, 720), new Size(1280, 850) })
                        {
                            form.Size = size;
                            foreach (var tab in Enum.GetValues<LauncherTab>())
                            {
                                form.Navigation.Focus();
                                while (form.Navigation.FocusedTab != tab) Command(form, Keys.D);
                                Command(form, Keys.Enter);
                                for (var frame = 0; form.Navigation.IsAnimating && frame < 100; frame++) await Task.Delay(10);
                                await Task.Delay(60);
                                CheckHeader(form);
                                var page = tab switch
                                {
                                    LauncherTab.Settings => (Control)Find<SettingsPage>(form).Single(),
                                    LauncherTab.Skins => Find<SkinsPage>(form).Single(),
                                    LauncherTab.Screenshots => Find<GalleryPage>(form).Single(),
                                    LauncherTab.Files => Find<GameFilesPage>(form).Single(),
                                    _ => Find<SkinScene>(form).First(s => s.Visible).Parent!
                                };
                                Check(page.Visible, "Selected tab is visible");
                                if (tab == LauncherTab.Settings)
                                {
                                    foreach (var control in Find<PixelButton>(page).Cast<Control>().Concat(Find<TextBox>(page)).Concat(Find<PixelComboBox>(page)).Concat(Find<CheckBox>(page))) CheckUnclipped(control);
                                    CheckSettingsOverlap(page);
                                }
                                if (tab == LauncherTab.Skins) CheckUnclipped(Find<PixelButton>(page).Single(b => b.Text == L.T("Применить")));
                                if (tab == LauncherTab.Eclipse)
                                {
                                    var play = Find<PixelButton>(page).Single(b => b.AccessibleName == L.T("Играть"));
                                    var model = Find<SkinScene>(page).Single();
                                    CheckUnclipped(play); CheckUnclipped(Find<PixelComboBox>(page).Single());
                                    Check(model.RectangleToScreen(model.ClientRectangle).Bottom <= play.RectangleToScreen(play.ClientRectangle).Top, "Character scene and Play do not overlap");
                                    CheckLaunchBadge(form);
                                }
                                Capture(form, screenshotDirectory, $"{tab.ToString().ToLowerInvariant()}-{size.Width}");
                                await CheckBusyControlsAsync(form, screenshotDirectory, $"busy-{tab.ToString().ToLowerInvariant()}-{size.Width}");
                            }
                        }
                        Console.WriteLine("PASS UI: original Steve, centered overlapping header, aligned tops, growing card heights, five tabs at 1040x720 and 1280x850, no settings clipping or keyboard hint");
                    }
                    catch (Exception ex) { Capture(form, screenshotDirectory, "failed"); Console.Error.WriteLine(string.Join(" | ", Find<Label>(form).Where(l => l.Visible).Select(l => l.Text))); failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) { Console.Error.WriteLine("UI tests timed out"); Environment.Exit(1); }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return 0;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task CheckSkinLibraryAsync(MainForm form, AppPaths paths, string screenshots)
    {
        var page = Find<SkinsPage>(form).Single();
        await page.ImportFileAsync(SkinProfileTests.Fixture(paths), true);
        var library = new SkinLibrary(paths);
        Check(library.All().Count == 1, "Imported skin appears in the saved library");
        Find<PixelButton>(page).Single(b => b.Text == L.T("Применить")).PerformClick();
        for (var i = 0; library.Selected() == null && i < 50; i++) await Task.Delay(20);
        Check(library.Selected()?.Variant == "SLIM", "Apply saves the local skin without Microsoft sign-in");
        for (var i = 0; typeof(MainForm).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form) != null && i < 50; i++) await Task.Delay(20);
        Capture(form, screenshots, "custom-skin");
        var standard = Find<TextureTile>(page).Single(t => t.Tag == null && t.AccessibleName == L.T("По умолчанию"));
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(standard, [EventArgs.Empty]);
        Find<PixelButton>(page).Single(b => b.Text == L.T("Применить")).PerformClick();
        for (var i = 0; typeof(MainForm).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form) != null && i < 50; i++) await Task.Delay(20);
        Check(library.Selected() == null && library.All().Count == 1, "Steve can be restored without removing the saved PNG");
        using var dialog = new ProfileEditDialog("Fixture"); dialog.Show(form);
        Check(Find<TextBox>(dialog).Single().MaxLength == 64, "Profile names are editable");
        foreach (var button in Find<PixelButton>(dialog)) Check(dialog.ClientRectangle.Contains(button.Bounds), "Profile actions fit in both languages");
        Capture(dialog, screenshots, "profile-dialog"); dialog.Close();
    }
    private static void CheckEffectPlacement(MainForm form)
    {
        var navigation = form.Navigation;
        using var image = new Bitmap(navigation.Width, navigation.Height);
        navigation.DrawToBitmap(image, navigation.ClientRectangle);
        var greenPixels = 0;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var color = image.GetPixel(x, y);
            if (!IsAmbientGreen(color)) continue;
            Check(y >= 84, "The solid dark header covers glow and particles");
            greenPixels++;
        }
        Check(greenPixels > 0, "The green effect is visible below the dark header");
        Check(image.GetPixel(4, 4).ToArgb() == PixelTheme.Header.ToArgb(), "Opaque header pixels stay dark over the effect");
        Check(navigation.Height == TabCarousel.DefaultHeight, "The atmosphere does not move page content");
        using var full = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(full, form.ClientRectangle);
        var origin = form.PointToClient(navigation.PointToScreen(Point.Empty));
        var bodyParticles = 0;
        for (var y = origin.Y + navigation.Height; y < origin.Y + HeaderParticles.EffectTop + HeaderParticles.BandHeight; y++)
        for (var x = 25; x < full.Width - 25; x++)
        {
            var color = full.GetPixel(x, y);
            if (IsAmbientGreen(color)) bodyParticles++;
        }
        Check(bodyParticles > 0, "Particles continue into the main scene toward Steve's head");
    }
    private static bool IsAmbientGreen(Color color) => color.G >= 27 && color.G > color.R * 1.3f && color.G > color.B * 1.45f;
    private static async Task CheckLanguageSwitchAsync(MainForm form, AppPaths paths, string originalLanguage)
    {
        var scene = Find<SkinScene>(form).Single();
        var pose = scene.Pose;
        form.Navigation.Open(LauncherTab.Settings);
        await Task.Delay(80);
        var page = Find<SettingsPage>(form).Single();
        var memory = Find<TextBox>(page).Single(t => t.AccessibleName == L.T("Память Minecraft, МБ"));
        var nickname = Find<TextBox>(page).Single(t => t.AccessibleName == L.T("Никнейм"));
        var selector = Find<PixelComboBox>(page).Single(c => c.AccessibleName == L.T("Язык"));
        var previousMemory = memory.Text; var previousName = nickname.Text;
        memory.Text = "7168"; nickname.Text = "Unsaved_Name";
        var other = originalLanguage == "en" ? "ru" : "en";
        selector.SelectedIndex = other == "ru" ? 1 : 0;
        Check(L.Language == other && JsonStore.Read<Settings>(paths.FilePath("settings.json"))!.Language == other, "Language selection applies and persists immediately");
        Check(memory.Text == "7168" && nickname.Text == "Unsaved_Name", "Switching language preserves unsaved input");
        Check(scene.Pose == pose, "Language switching must not recreate the character scene");
        Check(Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Играть")).Text == L.T("ИГРАТЬ"), "The existing home page updates in place");
        Check(Find<PixelButton>(page).Any(b => b.Text == L.T("Сохранить")), "Existing settings controls update in place");
        using (var userContent = new Panel())
        {
            var label = new Label { Text = L.T("Выберите файл") };
            var editor = new TextBox { Text = "Сохранить" };
            userContent.Controls.Add(label); userContent.Controls.Add(editor); L.CaptureTree(userContent);
            L.PreserveUserText(label); label.Text = "Settings";
            selector.SelectedIndex = originalLanguage == "ru" ? 1 : 0;
            L.RefreshTree(userContent);
            Check(label.Text == "Settings" && editor.Text == "Сохранить", "File names and editor contents are never translated");
        }
        memory.Text = previousMemory; nickname.Text = previousName;
        Check(L.Language == originalLanguage && JsonStore.Read<Settings>(paths.FilePath("settings.json"))!.Language == originalLanguage, "Both language directions persist");
        form.Navigation.Open(LauncherTab.Eclipse);
        await Task.Delay(250);
        form.Navigation.Focus();
        Console.WriteLine("PASS localization: live English/Russian switch, persistence, untouched input and character pose");
    }
    private sealed class UpdateHandler : HttpMessageHandler
    {
        internal readonly TaskCompletionSource<HttpResponseMessage> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Pending.Task.WaitAsync(ct);
    }
    private static async Task CheckUpdateAndHoursAsync(MainForm form, UpdateHandler handler)
    {
        var update = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Обновить лаунчер"));
        Check(!update.Visible && Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Играть")).Enabled, "A slow update server cannot delay Ready or Play");
        handler.Pending.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(LauncherFeatureTests.Metadata("v99.0.0")) });
        for (var i = 0; !update.Visible && i < 100; i++) await Task.Delay(10);
        Check(update.Visible && update.Enabled && update.Primary, "A newer release shows one green update button");
        var identity = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Настройки никнейма"));
        Check(update.Parent == identity.Parent && update.Right <= identity.Left, "Update sits to the left of nickname");
        Check(form.HoursBadge.AccessibleName.Contains(PlaytimeCounter.Format(4444.4 / 3600)), "Saved hours use the selected language");
        CheckLaunchBadge(form);
        Console.WriteLine("PASS updates/hours: asynchronous startup check, green header button, saved hours and overlapping Play badge");
    }
    private static void CheckLaunchBadge(MainForm form)
    {
        var play = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Играть"));
        var badge = form.HoursBadge;
        var version = Find<PixelComboBox>(play.Parent!).Single();
        Check(Rectangle.Intersect(play.Bounds, badge.Bounds).Height == 12, "Play slightly overlaps the hour plaque");
        Check(badge.Bounds.Bottom < version.Top && badge.Bounds.Left + badge.Bounds.Width / 2 == play.Left + play.Width / 2, "Hour plaque is centered and clears version selection");
        Check(play.Parent!.ClientRectangle.Contains(badge.Bounds), "The hour plaque stays inside the launch panel");
        using var expected = new Bitmap(play.Width, play.Height);
        using var frame = new Bitmap(form.Width, form.Height);
        play.DrawToBitmap(expected, play.ClientRectangle);
        form.DrawToBitmap(frame, form.ClientRectangle);
        var origin = form.PointToClient(play.PointToScreen(Point.Empty));
        for (var y = play.Height - 12; y < play.Height; y++)
        for (var x = 0; x < play.Width; x++)
            Check(expected.GetPixel(x, y).ToArgb() == frame.GetPixel(origin.X + x, origin.Y + y).ToArgb(), "Plaque must never cover Play's lower border");
    }
    private static async Task CheckInstallerAsync(MainForm owner, string directory)
    {
        var paths = new AppPaths(Path.Combine(directory, "installer-fixture"));
        JsonStore.Write(paths.FilePath("plans/fixture.json"), new InstallPlan { Version = "fixture", MainClass = "vanilla.Main", JavaRelativePath = "runtime/java.exe" });
        using var http = new HttpClient(new InstallerHandler());
        using var dialog = new VersionInstallerDialog(new ModLoaders(http, paths, new Minecraft(http, paths)), ["fixture"], "fixture");
        dialog.Show(owner);
        var install = Find<PixelButton>(dialog).Single(b => b.Text == L.T("Установить"));
        for (var i = 0; !install.Enabled && i < 100; i++) await Task.Delay(10);
        Check(install.Enabled, "Installer loads compatible loader versions");
        foreach (var control in dialog.Controls.Cast<Control>()) CheckUnclipped(control);
        CheckSettingsOverlap(dialog);
        Capture(dialog, directory, "version-installer");
        install.PerformClick();
        for (var i = 0; dialog.Visible && i < 200; i++) await Task.Delay(10);
        var installed = dialog.InstalledProfile ?? throw new InvalidOperationException("Installer did not return a profile");
        Check(installed.Id == "fabric-loader-1.2.3-fixture", "Install button creates a profile");
        Check(ModdedProfile.ReadAll(paths).Single().Id == installed.Id, "Installed profile is saved for the next launcher start");
        Check(Directory.Exists(Path.Combine(paths.Root, "instances", installed.Id, "mods")), "Installed profile has a mods folder");
        Console.WriteLine("PASS installer: loads choices, installs a profile, isolates mods and fits its controls");
    }
    private sealed class InstallerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("profile/json")
                ? "{\"id\":\"fabric-loader-1.2.3-fixture\",\"inheritsFrom\":\"fixture\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"libraries\":[]}"
                : "[{\"loader\":{\"version\":\"1.2.3\",\"stable\":true}}]";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private static async Task CheckIdlePosesAsync(MainForm form, string directory)
    {
        var scene = Find<SkinScene>(form).Single();
        var original = scene.Pose;
        try
        {
            foreach (var pose in Enum.GetValues<StandingPose>())
            {
                scene.SetPose(pose); scene.Refresh();
                var frames = scene.ModelFrames;
                var time = scene.IdleSeconds;
                await Task.Delay(180);
                Check(scene.Pose == pose && scene.IdleSeconds > time + .10, "The selected pose has a running idle phase");
                Check(scene.ModelFrames > frames + 6, "Idle updates the character rather than only the header");
                Capture(form, directory, "pose-" + pose.ToString().ToLowerInvariant());
            }
            Check(form.Icon != null, "The window has the embedded Eclipse icon");
            Console.WriteLine("PASS poses: both reference stances animate, pose remains selected, and the window icon is assigned");
        }
        finally { scene.SetPose(original); scene.Refresh(); }
    }
    private static void CheckCapeViews(MainForm form, string directory)
    {
        var scene = Find<SkinScene>(form).Single();
        var original = scene.Pose;
        using var cape = new Bitmap(64, 32);
        using (var g = Graphics.FromImage(cape))
        {
            g.Clear(Color.FromArgb(170, 35, 42));
            using var gold = new SolidBrush(Color.FromArgb(232, 191, 68));
            g.FillRectangle(gold, 1, 1, 10, 2);
            g.FillRectangle(gold, 12, 1, 10, 2);
            g.FillRectangle(gold, 5, 5, 2, 8);
            g.FillRectangle(gold, 16, 5, 2, 8);
            g.FillRectangle(gold, 1, 15, 10, 2);
            g.FillRectangle(gold, 12, 15, 10, 2);
        }
        var yaw = typeof(SkinScene).GetField("yaw", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousYaw = (float)yaw.GetValue(scene)!;
        try
        {
            foreach (var pose in Enum.GetValues<StandingPose>())
            foreach (var (angle, label) in new[] { (0f, "front"), (MathF.PI / 2, "side"), (MathF.PI, "back") })
            {
                yaw.SetValue(scene, angle);
                scene.SetPose(pose);
                scene.SetAppearance(null, false, cape);
                scene.Animate(Rectangle.Empty, 1d / 60);
                scene.Refresh();
                Capture(form, directory, $"cape-{pose.ToString().ToLowerInvariant()}-{label}");
            }
            Console.WriteLine("PASS cape: both poses rendered from front, side and back");
        }
        finally
        {
            yaw.SetValue(scene, previousYaw);
            scene.SetAppearance(null, false, null);
            scene.SetPose(original);
            scene.Refresh();
        }
    }
    private static void CheckSpaceLaunch(MainForm form)
    {
        var play = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Играть"));
        var versions = Find<PixelComboBox>(form).Single(c => c.AccessibleName == L.T("Версия Minecraft"));
        var operationField = typeof(MainForm).GetField("operation", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Check(operationField.GetValue(form) == null, "No operation is active before the hotkey test");
        using var guard = new CancellationTokenSource();
        // The existing operation guard isolates the button dispatch from real game/network startup.
        operationField.SetValue(form, guard);
        var clicks = 0;
        void Click(object? sender, EventArgs e) => clicks++;
        play.Click += Click;
        try
        {
            var selectedVersion = versions.SelectedItem;
            form.Navigation.Focus();
            Check(Command(form, Keys.Space) && clicks == 1, "Space activates Play once on the main page");
            Check(Equals(versions.SelectedItem, selectedVersion), "Space uses the currently selected version");
            play.Enabled = false;
            Check(Command(form, Keys.Space) && clicks == 1, "Space does not queue another launch while Play is disabled");
            play.Enabled = true; versions.Focus();
            Command(form, Keys.Space);
            Check(clicks == 1, "Space on the version selector does not launch the game");
            form.Navigation.Focus();
            Console.WriteLine("PASS hotkey: Space dispatches Play once, retains the version and respects busy controls and version selection");
        }
        finally { play.Click -= Click; play.Enabled = true; operationField.SetValue(form, null); }
    }
    private static void CheckSoundFeedback()
    {
        var cues = new List<UiCue>();
        void Record(UiCue cue) => cues.Add(cue);
        UiSounds.Enabled = true; UiSounds.Requested += Record;
        try
        {
            using var button = new PixelButton();
            var click = typeof(PixelButton).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!;
            click.Invoke(button, [EventArgs.Empty]);
            Check(cues.SequenceEqual([UiCue.Click]), "Buttons request one click cue");
            button.Primary = true; click.Invoke(button, [EventArgs.Empty]);
            Check(cues.Last() == UiCue.Confirm, "Primary buttons request confirmation");
            var count = cues.Count;
            button.Enabled = false; click.Invoke(button, [EventArgs.Empty]);
            Check(cues.Count == count, "Disabled buttons stay silent");
            UiSounds.Enabled = false; button.Enabled = true; click.Invoke(button, [EventArgs.Empty]);
            Check(cues.Count == count, "Muted controls stay silent");
            UiSounds.Enabled = true;
            using var navigation = new TabCarousel();
            navigation.MoveSelection(1); navigation.Commit();
            Check(cues.TakeLast(2).SequenceEqual([UiCue.Select, UiCue.Confirm]), "Keyboard tab selection and commit have separate cues");
            using var slider = new PixelSlider();
            count = cues.Count; slider.SetValue(6144);
            Check(cues.Count == count, "Programmatic slider binding stays silent");
            typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(slider, [new KeyEventArgs(Keys.Right)]);
            Check(cues.Count == count + 1 && cues.Last() == UiCue.Tick, "Keyboard slider adjustment clicks once");
            Console.WriteLine("PASS sound routing: buttons, disabled and muted controls, keyboard tabs, slider input and silent binding");
        }
        finally { UiSounds.Requested -= Record; UiSounds.Enabled = true; }
    }
    private static async Task CheckRotationAsync(MainForm form, string directory)
    {
        var scene = Find<SkinScene>(form).Single();
        var mouseDown = typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mouseMove = typeof(Control).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mouseUp = typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var reset = typeof(Control).GetMethod("OnMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steps = 0;
        void Rotate()
        {
            // A high polling rate must not trigger a separate render per mouse message.
            for (var sample = 0; sample < 8; sample++)
                mouseMove.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 0, 100 + ++steps, 100, 0)]);
        }
        mouseDown.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0)]);
        form.Navigation.FrameStarting += Rotate;
        try
        {
            await Task.Delay(500);
            var frames = scene.ModelFrames;
            var platformTicks = scene.PlatformTicks; var characterTicks = scene.CharacterTicks; var outlineTicks = scene.OutlineTicks;
            var headerFrames = form.Navigation.PaintCount;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(1500);
            var seconds = watch.Elapsed.TotalSeconds;
            var rotationFps = (scene.ModelFrames - frames) / seconds;
            var headerFps = (form.Navigation.PaintCount - headerFrames) / seconds;
            var allocatedBytesPerSecond = (GC.GetAllocatedBytesForCurrentThread() - allocated) / seconds;
            var millisecondsPerTick = 1000d / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, scene.ModelFrames - frames);
            Console.WriteLine($"RENDER ms/frame: platform {(scene.PlatformTicks - platformTicks) * millisecondsPerTick:F2}, character {(scene.CharacterTicks - characterTicks) * millisecondsPerTick:F2}, outline {(scene.OutlineTicks - outlineTicks) * millisecondsPerTick:F2}");
            JsonStore.Write(Path.Combine(directory, "rotation.json"), new { rotationFps, headerFps, allocatedBytesPerSecond, measuredSeconds = seconds });
            Console.WriteLine($"ROTATION: {rotationFps:F1} new poses/s, header {headerFps:F1} FPS, {allocatedBytesPerSecond / 1024 / 1024:F1} MiB allocated/s");
            Check(rotationFps >= 105, $"Dragging should keep rendering new poses near 120 FPS; measured {rotationFps:F1}");
            Check(headerFps >= 105, $"Header stays smooth while dragging; measured {headerFps:F1}");
            Check(allocatedBytesPerSecond < 8 * 1024 * 1024, "Dragging reuses image and contour buffers instead of allocating them per pose");
        }
        finally
        {
            form.Navigation.FrameStarting -= Rotate;
            mouseUp.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 1, 100, 100, 0)]);
            reset.Invoke(scene, [new MouseEventArgs(MouseButtons.Left, 2, 100, 100, 0)]);
            scene.Refresh();
        }
    }
    private static async Task CheckAnimationAsync(MainForm form, string directory)
    {
        Check(form.Navigation.AnimationRunning, "Animation runs while the launcher is visible");
        await Task.Delay(1200);
        var before = form.Navigation.PaintCount;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        await Task.Delay(1500);
        var elapsed = watch.Elapsed.TotalSeconds;
        var fps = (form.Navigation.PaintCount - before) / elapsed;
        var cpuMilliseconds = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        Check(form.Navigation.AnimationFailure == null, "High-resolution animation clock has no native error");
        Check(fps >= 105 && fps <= 135, $"Actual header paints should track 120 FPS; measured {fps:F1}");
        Check(form.Navigation.ParticleCount > 0, "Visible header emits particles");
        JsonStore.Write(Path.Combine(directory, "animation.json"), new { targetFps = AnimationClock.FramesPerSecond, measuredFps = fps, measuredSeconds = elapsed, cpuMilliseconds, liveParticles = form.Navigation.ParticleCount });
        var oldState = form.WindowState;
        form.WindowState = FormWindowState.Minimized;
        await Task.Delay(80);
        Check(!form.Navigation.AnimationRunning, "Animation sleeps when the launcher is minimized");
        var pausedScene = Find<SkinScene>(form).Single();
        var pausedIdle = pausedScene.IdleSeconds;
        var stopped = form.Navigation.PaintCount;
        await Task.Delay(150);
        Check(form.Navigation.PaintCount - stopped <= 1, "No continuous redraw while minimized");
        Check(pausedScene.IdleSeconds == pausedIdle, "Character idle pauses while minimized");
        form.WindowState = oldState;
        await Task.Delay(100);
        Check(form.Navigation.AnimationRunning && form.Navigation.PaintCount > stopped, "Animation resumes after restoring the window");
        Console.WriteLine($"PASS animation: {fps:F1} actual FPS, {cpuMilliseconds / elapsed:F0} CPU ms/s; pauses minimized");
    }
    private static async Task CheckBusyControlsAsync(MainForm form, string directory, string imageName)
    {
        Func<CancellationToken, Task> action = ct => Task.Delay(Timeout.Infinite, ct);
        var pending = (Task)typeof(MainForm).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [action])!;
        await Task.Delay(30);
        var cancel = Find<PixelButton>(form).Single(b => b.Text == L.T("Отмена"));
        Check(cancel.Visible && cancel.Enabled, "Cancellation is available during an active operation");
        CheckUnclipped(cancel);
        var status = cancel.Parent!.Controls.OfType<Label>().Single();
        Check(!cancel.Bounds.IntersectsWith(status.Bounds), "Cancel and status do not overlap");
        var login = Find<PixelButton>(form).Single(b => b.AccessibleName == L.T("Войти через Microsoft") || b.AccessibleName == L.T("Настройки никнейма"));
        var chromeButtons = login.Parent!.Controls.OfType<PixelButton>().Where(b => b.Visible).ToArray();
        foreach (var button in chromeButtons) CheckUnclipped(button);
        for (var i = 0; i < chromeButtons.Length; i++)
        for (var j = i + 1; j < chromeButtons.Length; j++)
            Check(!chromeButtons[i].Bounds.IntersectsWith(chromeButtons[j].Bounds), "Account and window buttons do not overlap");
        Capture(form, directory, imageName);
        cancel.PerformClick();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!cancel.Visible, "Cancellation disappears after the operation");
    }
    private static void CheckHeader(MainForm form)
    {
        var cards = form.Navigation.CurrentCards;
        var front = cards.Single(c => c.Tab == form.Navigation.FocusedTab);
        Check(Math.Abs(front.Bounds.Left + front.Bounds.Width / 2f - form.Navigation.Width / 2f) <= 1, "Selected header card is centered");
        Check(cards.Select(c => c.Bounds.Top).Distinct().Count() == 1, "Header card tops share one line");
        Check(cards.All(c => c.Bounds.Height >= TabCarousel.BaseCardHeight), "No card shrinks below the default height");
        Check(cards.Select(c => c.Bounds.Width).Distinct().Count() == 1, "Far cards retain their default width");
        foreach (var card in cards.Where(c => c.Depth > 1.5f)) Check(card.Bounds.Height == TabCarousel.BaseCardHeight, "Far cards retain their default height");
        foreach (var card in cards.Where(c => c.Depth > .5f && c.Depth < 1.5f))
        {
            Check(card.Bounds.Height > TabCarousel.BaseCardHeight && card.Bounds.Height < front.Bounds.Height, "Near cards grow down between far and selected sizes");
            Check(Rectangle.Intersect(front.Bounds, card.Bounds).Width > 0, "Near cards overlap below the selected card");
        }
        Check(cards.All(c => c.Bounds.Left >= 44 && c.Bounds.Right <= form.Navigation.Width - 44), "Cards do not overlap header arrows");
    }
    private static void CheckUnclipped(Control control)
    {
        var bounds = control.RectangleToScreen(control.ClientRectangle);
        for (var ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
            Check(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(bounds), $"{control.Text} must stay inside {ancestor.GetType().Name}");
    }
    private static void CheckSettingsOverlap(Control parent)
    {
        var children = parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
        for (var i = 0; i < children.Length; i++)
        for (var j = i + 1; j < children.Length; j++)
            Check(!children[i].Bounds.IntersectsWith(children[j].Bounds), $"Settings controls overlap: {children[i].Text} / {children[j].Text}");
        foreach (var child in children) CheckSettingsOverlap(child);
    }
    private static bool Command(MainForm form, Keys key)
    {
        var message = Message.Create(form.Handle, 0x100, (IntPtr)key, IntPtr.Zero);
        return (bool)typeof(MainForm).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [message, key])!;
    }
    private static IEnumerable<T> Find<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Form form, string directory, string name)
    {
        form.Refresh(); using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, form.ClientRectangle); image.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
    }
    private static byte[] Snapshot(Control control)
    {
        using var image = new Bitmap(control.Width, control.Height); control.DrawToBitmap(image, control.ClientRectangle);
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png);
        return System.Security.Cryptography.SHA256.HashData(stream.ToArray());
    }
    private static void CheckCyrillic(Form form)
    {
        const string letters = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        using var graphics = form.CreateGraphics(); var dc = graphics.GetHdc(); var font = PixelTheme.Font().ToHfont(); var old = SelectObject(dc, font);
        try { var glyphs = new ushort[letters.Length]; GetGlyphIndices(dc, letters, letters.Length, glyphs, 1); Check(glyphs.All(g => g != ushort.MaxValue), "Pixel font contains all Russian glyphs"); }
        finally { SelectObject(dc, old); DeleteObject(font); graphics.ReleaseHdc(dc); }
    }
}
