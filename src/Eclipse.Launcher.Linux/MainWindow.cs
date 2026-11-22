using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Eclipse;

public sealed class LauncherConfig
{
    public string MicrosoftClientId { get; set; } = "";
}

internal sealed class MainWindow : Window
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#111512"));
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#1A211B"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#0B0F0C"));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#4E8A35"));
    private static readonly IBrush AccentHoverBrush = new SolidColorBrush(Color.Parse("#5B9B40"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#F3F5EC"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9AA592"));
    private static readonly IBrush EdgeBrush = new SolidColorBrush(Color.Parse("#334034"));

    private readonly AppPaths paths;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ComboBox versions = new() { MinWidth = 330 };
    private readonly ComboBox loginMode = new() { MinWidth = 220 };
    private readonly ComboBox language = new() { MinWidth = 160 };
    private readonly TextBox nickname = new() { MaxLength = 16, MinWidth = 220 };
    private readonly TextBox memory = new() { MinWidth = 110 };
    private readonly TextBox width = new() { MinWidth = 90 };
    private readonly TextBox height = new() { MinWidth = 90 };
    private readonly CheckBox automatic = new();
    private readonly CheckBox fullscreen = new();
    private readonly CheckBox minimize = new();
    private readonly Button play;
    private readonly Button signIn;
    private readonly Button save;
    private readonly Button fabric;
    private readonly Button quilt;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = MutedBrush };
    private readonly TextBlock hours = new() { Foreground = MutedBrush, FontSize = 14 };
    private Settings settings = new() { AutomaticMemory = true };
    private HttpClient? http;
    private Authentication? auth;
    private Minecraft? minecraft;
    private CancellationTokenSource? operation;
    private bool gameRunning;
    private string selectedVersion = "";

    internal MainWindow(AppPaths paths)
    {
        this.paths = paths;
        Title = "Eclipse — Minecraft Java";
        Width = 1180;
        Height = 780;
        MinWidth = 940;
        MinHeight = 680;
        Background = BackgroundBrush;
        Foreground = TextBrush;
        FontFamily = new FontFamily("avares://Eclipse/Assets/Monocraft.ttf#Monocraft");
        try { using var icon = AssetLoader.Open(new Uri("avares://Eclipse/Assets/Eclipse.ico")); Icon = new WindowIcon(icon); } catch { }

        play = PixelButton("PLAY", true, 24);
        signIn = PixelButton("MICROSOFT", false);
        save = PixelButton("SAVE", true);
        fabric = PixelButton("+ FABRIC", false);
        quilt = PixelButton("+ QUILT", false);

        Content = BuildLayout();
        if (Program.SmokeTest)
            Opened += (_, _) => DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(750));
        else
            Opened += async (_, _) => await InitializeAsync();
        Closing += (_, e) =>
        {
            if (gameRunning) { e.Cancel = true; WindowState = WindowState.Minimized; return; }
            operation?.Cancel(); lifetime.Cancel();
        };
        Closed += (_, _) => { lifetime.Dispose(); http?.Dispose(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && play.IsEnabled) { play.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
        };

        play.Click += async (_, _) => await RunOperationAsync(PlayAsync);
        signIn.Click += async (_, _) => await RunOperationAsync(SignInAsync);
        save.Click += (_, _) => SaveSettings();
        fabric.Click += async (_, _) => await RunOperationAsync(ct => InstallLoaderAsync("fabric", ct));
        quilt.Click += async (_, _) => await RunOperationAsync(ct => InstallLoaderAsync("quilt", ct));
        versions.SelectionChanged += (_, _) => ChangeVersion();
        loginMode.SelectionChanged += (_, _) => RefreshLoginMode();
        automatic.IsCheckedChanged += (_, _) => RefreshMemoryMode();
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedIndex < 0) return;
            L.SetLanguage(language.SelectedIndex == 1 ? "ru" : "en");
            ApplyLanguage();
        };
    }

    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("92,*,50") };
        var header = new Border { Background = HeaderBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(28, 16) };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var brand = new TextBlock { Text = "ECLIPSE", FontSize = 28, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        var subtitle = new TextBlock { Text = "ARCH LINUX", FontSize = 12, Foreground = MutedBrush, Margin = new Thickness(18, 0), VerticalAlignment = VerticalAlignment.Center };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var screenshots = PixelButton("SCREENSHOTS", false, 12);
        var files = PixelButton("FILES", false, 12);
        var releases = PixelButton("UPDATES", false, 12);
        screenshots.Click += (_, _) => OpenFolder(Path.Combine(paths.Game, "screenshots"));
        files.Click += (_, _) => OpenFolder(paths.Game);
        releases.Click += (_, _) => OpenUrl("https://github.com/deluti/eclipse-launcher/releases/latest");
        tools.Children.Add(screenshots); tools.Children.Add(files); tools.Children.Add(releases);
        Grid.SetColumn(subtitle, 1); Grid.SetColumn(tools, 2);
        headerGrid.Children.Add(brand); headerGrid.Children.Add(subtitle); headerGrid.Children.Add(tools); header.Child = headerGrid;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,370"), Margin = new Thickness(28, 20, 28, 14) };
        var main = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto") };
        var preview = new StevePreview { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8) };
        versionRow.Children.Add(versions); versionRow.Children.Add(fabric); versionRow.Children.Add(quilt);
        play.Width = 390; play.Height = 68; play.HorizontalAlignment = HorizontalAlignment.Center; play.Margin = new Thickness(0, 8, 0, 0);
        hours.HorizontalAlignment = HorizontalAlignment.Center; hours.Margin = new Thickness(0, 8, 0, 2);
        Grid.SetRow(versionRow, 1); Grid.SetRow(play, 2); Grid.SetRow(hours, 3);
        main.Children.Add(preview); main.Children.Add(versionRow); main.Children.Add(play); main.Children.Add(hours);

        var settingsPanel = new Border { Background = PanelBrush, BorderBrush = EdgeBrush, BorderThickness = new Thickness(2), Padding = new Thickness(22) };
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(Heading("LAUNCH SETTINGS"));
        form.Children.Add(Label("ACCOUNT"));
        loginMode.ItemsSource = new[] { "LOCAL NICKNAME", "MICROSOFT" }; form.Children.Add(loginMode);
        form.Children.Add(nickname);
        signIn.HorizontalAlignment = HorizontalAlignment.Stretch; form.Children.Add(signIn);
        form.Children.Add(Label("MINECRAFT MEMORY (MB)"));
        var memoryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        automatic.Content = "AUTO"; automatic.VerticalAlignment = VerticalAlignment.Center;
        memoryRow.Children.Add(automatic); memoryRow.Children.Add(memory); form.Children.Add(memoryRow);
        form.Children.Add(Label("WINDOW"));
        var resolution = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        resolution.Children.Add(width); resolution.Children.Add(new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }); resolution.Children.Add(height);
        form.Children.Add(resolution);
        fullscreen.Content = "FULLSCREEN"; minimize.Content = "MINIMIZE ECLIPSE AFTER PLAY";
        form.Children.Add(fullscreen); form.Children.Add(minimize);
        form.Children.Add(Label("LANGUAGE"));
        language.ItemsSource = new[] { "English", "Русский" }; form.Children.Add(language);
        save.HorizontalAlignment = HorizontalAlignment.Stretch; save.Margin = new Thickness(0, 8, 0, 0); form.Children.Add(save);
        settingsPanel.Child = form;
        Grid.SetColumn(settingsPanel, 1); body.Children.Add(main); body.Children.Add(settingsPanel);

        var footer = new Border { Background = HeaderBrush, Padding = new Thickness(24, 10), BorderBrush = EdgeBrush, BorderThickness = new Thickness(0, 2, 0, 0), Child = status };
        Grid.SetRow(body, 1); Grid.SetRow(footer, 2); root.Children.Add(header); root.Children.Add(body); root.Children.Add(footer);
        return root;
    }

    private static TextBlock Heading(string value) => new() { Text = value, FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 6) };
    private static TextBlock Label(string value) => new() { Text = value, FontSize = 12, Foreground = MutedBrush, Margin = new Thickness(0, 6, 0, 0) };
    private static Button PixelButton(string text, bool primary, double size = 14)
    {
        var button = new Button
        {
            Content = text, FontSize = size, FontWeight = FontWeight.Bold, Foreground = TextBrush,
            Background = primary ? AccentBrush : PanelBrush, BorderBrush = primary ? new SolidColorBrush(Color.Parse("#182416")) : EdgeBrush,
            BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(0), Padding = new Thickness(16, 9),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        return button;
    }

    private async Task InitializeAsync()
    {
        try
        {
            settings = await Task.Run(() => JsonStore.Read<Settings>(paths.FilePath("settings.json")) ?? new Settings { AutomaticMemory = true });
            L.SetLanguage(settings.Language);
            BindSettings();
            var config = await Task.Run(() => JsonStore.Read<LauncherConfig>(Path.Combine(AppContext.BaseDirectory, "launcher-config.json")) ?? new());
            http = Network.Create();
            auth = new Authentication(http, paths, config.MicrosoftClientId);
            minecraft = new Minecraft(http, paths);
            minecraft.GameStarted += () => Dispatcher.UIThread.Post(() =>
            {
                gameRunning = true;
                if (settings.MinimizeOnLaunch) WindowState = WindowState.Minimized;
            });
            await LoadVersionsAsync(lifetime.Token);
            RefreshHours();
            SetStatus(L.T("Готов к запуску"));
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void BindSettings()
    {
        loginMode.SelectedIndex = settings.OfflineMode ? 0 : 1;
        nickname.Text = settings.Nickname;
        automatic.IsChecked = settings.AutomaticMemory;
        memory.Text = settings.MemoryMb.ToString(L.Culture);
        width.Text = settings.WindowWidth.ToString(L.Culture);
        height.Text = settings.WindowHeight.ToString(L.Culture);
        fullscreen.IsChecked = settings.Fullscreen;
        minimize.IsChecked = settings.MinimizeOnLaunch;
        language.SelectedIndex = settings.Language == "ru" ? 1 : 0;
        RefreshLoginMode(); RefreshMemoryMode(); ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        play.Content = L.T("ИГРАТЬ");
        save.Content = L.T("Сохранить");
        fabric.Content = "+ FABRIC"; quilt.Content = "+ QUILT";
        RefreshHours();
    }

    private void RefreshLoginMode()
    {
        var offline = loginMode.SelectedIndex != 1;
        nickname.IsEnabled = offline;
        signIn.IsVisible = !offline;
        signIn.Content = string.IsNullOrEmpty(auth?.SavedName) ? L.T("Войти через Microsoft") : auth.SavedName;
    }
    private void RefreshMemoryMode() => memory.IsEnabled = automatic.IsChecked != true;

    private void SaveSettings()
    {
        try { ReadAndSaveSettings(); SetStatus(L.T("Настройки сохранены")); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void ReadAndSaveSettings()
    {
        settings.OfflineMode = loginMode.SelectedIndex != 1;
        settings.Nickname = settings.OfflineMode ? OfflineAccount.Create(nickname.Text ?? "").Name : settings.Nickname;
        if (!int.TryParse(memory.Text, out var memoryMb) || memoryMb is < 1024 or > 32768) throw new InvalidOperationException(L.T("Введите память от 1024 до 32768 МБ"));
        if (!int.TryParse(width.Text, out var windowWidth) || !int.TryParse(height.Text, out var windowHeight)) throw new InvalidOperationException("Invalid window size.");
        settings.MemoryMb = memoryMb; settings.AutomaticMemory = automatic.IsChecked == true;
        settings.WindowWidth = Math.Clamp(windowWidth, 640, 7680); settings.WindowHeight = Math.Clamp(windowHeight, 360, 4320);
        settings.Fullscreen = fullscreen.IsChecked == true; settings.MinimizeOnLaunch = minimize.IsChecked == true;
        settings.Language = language.SelectedIndex == 1 ? "ru" : "en";
        settings.Version = VersionId(versions.SelectedItem);
        JsonStore.Write(paths.FilePath("settings.json"), settings);
    }

    private async Task LoadVersionsAsync(CancellationToken ct, string? select = null)
    {
        if (minecraft == null) return;
        SetStatus(L.T("Загрузка версий…"));
        var manifest = await minecraft.VersionsAsync(false, ct);
        var releases = manifest.GetProperty("versions").EnumerateArray()
            .Where(value => value.Text("type") == "release" && SupportedRelease(value.Text("id"))).Select(value => value.Text("id")).ToArray();
        var profiles = ModdedProfile.ReadAll(paths);
        var items = new List<object>(); items.AddRange(profiles); items.AddRange(releases.Where(id => profiles.All(profile => profile.Id != id)));
        versions.ItemsSource = items;
        var wanted = select ?? settings.Version;
        versions.SelectedItem = items.FirstOrDefault(item => VersionId(item) == wanted) ?? items.FirstOrDefault();
        ChangeVersion();
    }

    private void ChangeVersion()
    {
        var next = VersionId(versions.SelectedItem);
        if (string.IsNullOrEmpty(next)) return;
        selectedVersion = next;
        paths.UseGameDirectory(versions.SelectedItem is ModdedProfile profile ? profile.GameDirectory : "");
    }

    private async Task SignInAsync(CancellationToken ct)
    {
        var account = await auth!.AuthenticateAsync(true, new Progress<string>(message => SetStatus(message)), ct);
        signIn.Content = account.Name;
        SetStatus(L.T("Лицензия подтверждена"));
    }

    private async Task PlayAsync(CancellationToken ct)
    {
        ReadAndSaveSettings();
        var version = VersionId(versions.SelectedItem);
        if (string.IsNullOrEmpty(version)) throw new InvalidOperationException(L.T("Выберите версию Minecraft"));
        minecraft!.MarkLaunchRequested();
        var progress = new Progress<string>(message => SetStatus(message));
        var offline = settings.OfflineMode ? OfflineAccount.Create(settings.Nickname) : null;
        var account = offline == null ? await auth!.AuthenticateAsync(false, progress, ct) : null;
        var plan = await minecraft.PrepareAsync(version, progress, ct);
        var exit = offline != null
            ? await minecraft.LaunchOfflineAsync(plan, offline, settings.MemoryMb, progress, ct, settings)
            : await minecraft.LaunchAsync(plan, account!, settings.MemoryMb, progress, ct, settings);
        gameRunning = false;
        RefreshHours();
        if (WindowState == WindowState.Minimized && settings.MinimizeOnLaunch) WindowState = WindowState.Normal;
        SetStatus(exit == 0 ? L.T("Minecraft закрыт") : L.F("Minecraft завершился с кодом {0}", exit), exit != 0);
    }

    private async Task InstallLoaderAsync(string loader, CancellationToken ct)
    {
        var selected = versions.SelectedItem;
        var gameVersion = selected is ModdedProfile profile ? profile.GameVersion : VersionId(selected);
        if (string.IsNullOrEmpty(gameVersion)) throw new InvalidOperationException(L.T("Выберите версию Minecraft"));
        SetStatus($"{loader}: {gameVersion}…");
        var manager = new ModLoaders(http!, paths, minecraft!);
        var choices = await manager.VersionsAsync(loader, gameVersion, ct);
        var choice = choices.FirstOrDefault(item => item.Stable) ?? choices.FirstOrDefault() ?? throw new InvalidOperationException("No compatible loader version.");
        var installed = await manager.InstallAsync(loader, gameVersion, choice.Version, new Progress<string>(message => SetStatus(message)), ct);
        await LoadVersionsAsync(ct, installed.Version);
        SetStatus($"{loader} {choice.Version} · {gameVersion}");
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (operation != null || auth == null || minecraft == null) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { SetStatus(L.T("Операция отменена или истекло время ожидания")); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally
        {
            gameRunning = false;
            operation.Dispose(); operation = null;
            if (!lifetime.IsCancellationRequested) SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        play.IsEnabled = signIn.IsEnabled = save.IsEnabled = fabric.IsEnabled = quilt.IsEnabled = versions.IsEnabled = !value;
        loginMode.IsEnabled = language.IsEnabled = !value;
        nickname.IsEnabled = !value && loginMode.SelectedIndex != 1;
        automatic.IsEnabled = fullscreen.IsEnabled = minimize.IsEnabled = !value;
        memory.IsEnabled = !value && automatic.IsChecked != true;
        width.IsEnabled = height.IsEnabled = !value;
        play.Content = value ? L.T("ПОДГОТОВКА…") : L.T("ИГРАТЬ");
    }

    private void RefreshHours() => hours.Text = minecraft == null ? "0.0 h" : PlaytimeCounter.Format(minecraft.Playtime.TotalHours);
    private void SetStatus(string message, bool error = false)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => SetStatus(message, error)); return; }
        status.Text = message; status.Foreground = error ? new SolidColorBrush(Color.Parse("#E3A27D")) : MutedBrush;
    }

    private void OpenFolder(string path) { Directory.CreateDirectory(path); OpenUrl("file://" + Uri.EscapeDataString(path).Replace("%2F", "/")); }
    private static void OpenUrl(string url)
    {
        var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        info.ArgumentList.Add(url); Process.Start(info)?.Dispose();
    }
    private static string VersionId(object? value) => value switch { ModdedProfile profile => profile.Id, string id => id, _ => "" };
    private static bool SupportedRelease(string version) => !version.StartsWith("1.", StringComparison.Ordinal) || Version.TryParse(version, out var parsed) && parsed.Minor >= 18;
}
