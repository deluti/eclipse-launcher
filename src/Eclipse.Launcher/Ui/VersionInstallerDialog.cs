namespace Eclipse;

internal sealed class VersionInstallerDialog : Form
{
    private readonly ModLoaders loaders;
    private readonly PixelComboBox game = new() { Width = 300, AccessibleName = "Версия Minecraft для установки" };
    private readonly PixelComboBox loader = new() { Width = 300, AccessibleName = "Загрузчик модов" };
    private readonly PixelComboBox version = new() { Width = 300, AccessibleName = "Версия загрузчика" };
    private readonly PixelButton install = new() { Text = "Установить", Primary = true, Enabled = false, Bounds = new Rectangle(30, 312, 260, 48) };
    private readonly PixelButton cancel = new() { Text = "Закрыть", Bounds = new Rectangle(330, 312, 220, 48) };
    private readonly Label status = new() { AutoSize = false, Bounds = new Rectangle(30, 250, 520, 46), ForeColor = PixelTheme.Muted };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? catalog;
    private CancellationTokenSource? installation;
    private bool closeWhenDone;
    private bool resourcesDisposed;
    private int generation;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal ModdedProfile? InstalledProfile { get; private set; }

    internal VersionInstallerDialog(ModLoaders loaders, IEnumerable<string> games, string selectedGame)
    {
        this.loaders = loaders;
        Text = "Установить версию"; Font = PixelTheme.Font(); ForeColor = PixelTheme.Text; BackColor = PixelTheme.Background;
        ClientSize = new Size(580, 390); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false;
        var heading = PixelTheme.TextLabel("УСТАНОВИТЬ ВЕРСИЮ", 20); heading.Location = new Point(30, 24);
        var gameLabel = PixelTheme.TextLabel("MINECRAFT"); gameLabel.Location = new Point(30, 86); game.Location = new Point(250, 76);
        var loaderLabel = PixelTheme.TextLabel("ЗАГРУЗЧИК"); loaderLabel.Location = new Point(30, 138); loader.Location = new Point(250, 128);
        var versionLabel = PixelTheme.TextLabel("ВЕРСИЯ"); versionLabel.Location = new Point(30, 190); version.Location = new Point(250, 180);
        game.Items.AddRange(games.Cast<object>()); game.SelectedItem = selectedGame;
        if (game.SelectedIndex < 0 && game.Items.Count > 0) game.SelectedIndex = 0;
        loader.Items.AddRange(["Fabric", "Quilt"]); loader.SelectedIndex = 0;
        Controls.AddRange([heading, gameLabel, game, loaderLabel, loader, versionLabel, version, status, install, cancel]);
        game.SelectedIndexChanged += async (_, _) => await LoadCatalogAsync();
        loader.SelectedIndexChanged += async (_, _) => await LoadCatalogAsync();
        Shown += async (_, _) => await LoadCatalogAsync();
        install.Click += async (_, _) => await InstallAsync();
        cancel.Click += (_, _) => { if (installation != null) installation.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (installation != null) { e.Cancel = true; closeWhenDone = true; installation.Cancel(); } };
        FormClosed += (_, _) => { if (!resourcesDisposed) { lifetime.Cancel(); catalog?.Cancel(); } };
    }
    private string Loader => loader.SelectedIndex == 1 ? "quilt" : "fabric";
    private async Task LoadCatalogAsync()
    {
        if (installation != null || game.SelectedItem is not string gameVersion) return;
        catalog?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        catalog = request;
        var current = ++generation;
        install.Enabled = false; version.Items.Clear(); version.SelectedIndex = -1;
        status.Text = "Получение версий загрузчика…";
        try
        {
            var available = await loaders.VersionsAsync(Loader, gameVersion, request.Token);
            if (IsDisposed || current != generation) return;
            version.Items.AddRange(available.Cast<object>());
            version.SelectedIndex = available.Count > 0 ? 0 : -1;
            install.Enabled = available.Count > 0;
            status.Text = available.Count > 0 ? "" : "Для этой версии загрузчик пока недоступен";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or ServiceException or InvalidDataException or System.Text.Json.JsonException)
        { if (!IsDisposed && current == generation) status.Text = ex.Message; }
        finally { if (catalog == request) catalog = null; request.Dispose(); }
    }
    private async Task InstallAsync()
    {
        if (installation != null || game.SelectedItem is not string gameVersion || version.SelectedItem is not LoaderVersion choice) return;
        var loaderName = Loader;
        var success = false;
        installation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        game.Enabled = loader.Enabled = version.Enabled = install.Enabled = false; cancel.Text = "Отмена";
        try
        {
            var progress = new Progress<string>(text => { if (!IsDisposed) status.Text = text; });
            var token = installation.Token;
            var plan = await Task.Run(() => loaders.InstallAsync(loaderName, gameVersion, choice.Version, progress, token), token);
            InstalledProfile = new(plan.Version, gameVersion, loaderName, choice.Version);
            success = true;
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Установка отменена"; }
        catch (Exception ex) when (ex is HttpRequestException or ServiceException or InvalidDataException or IOException or System.Text.Json.JsonException)
        { UiSounds.Play(UiCue.Error); if (!IsDisposed) status.Text = ex.Message; }
        finally
        {
            installation.Dispose(); installation = null;
            if (!IsDisposed)
            {
                game.Enabled = loader.Enabled = version.Enabled = true; install.Enabled = version.SelectedItem != null; cancel.Text = "Закрыть";
                if (success) { DialogResult = DialogResult.OK; Close(); }
                else if (closeWhenDone) Close();
            }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; lifetime.Cancel(); catalog?.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
