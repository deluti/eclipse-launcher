namespace Eclipse;

internal sealed class ProfileEditDialog : Form
{
    private readonly TextBox name = new() { BorderStyle = BorderStyle.None, MaxLength = 64, AccessibleName = L.T("Название профиля") };
    private readonly CheckBox content = new() { Text = L.T("Копировать моды и настройки"), Checked = true, AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly CheckBox worlds = new() { Text = L.T("Копировать миры"), AutoSize = true, FlatStyle = FlatStyle.Flat };
    internal string ProfileName => name.Text.Trim();
    internal bool CopyContent => content.Checked;
    internal bool CopyWorlds => worlds.Checked;
    internal ProfileEditDialog(string currentName)
    {
        Text = L.T("Профили"); BackColor = PixelTheme.Background; ForeColor = PixelTheme.Text; Font = PixelTheme.Font();
        FormBorderStyle = FormBorderStyle.FixedDialog; ClientSize = new Size(570, 300);
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var title = PixelTheme.TextLabel(L.T("Название профиля"), 18); title.Location = new Point(28, 26);
        PixelTheme.Style(name); name.Bounds = new Rectangle(28, 65, 510, 34); name.Text = currentName;
        PixelTheme.Style(content); content.Location = new Point(28, 126);
        PixelTheme.Style(worlds); worlds.Location = new Point(28, 170);
        var copy = new PixelButton { Text = L.T("Создать копию"), Primary = true, Bounds = new Rectangle(28, 230, 210, 42) };
        var rename = new PixelButton { Text = L.T("Переименовать"), Bounds = new Rectangle(250, 230, 170, 42) };
        var close = new PixelButton { Text = L.T("Закрыть"), Bounds = new Rectangle(432, 230, 110, 42), DialogResult = DialogResult.Cancel };
        void Finish(DialogResult result)
        {
            try { _ = GameProfiles.ValidateName(name.Text); DialogResult = result; Close(); }
            catch (InvalidOperationException ex) { PixelDialog.Message(this, L.T("Профили"), ex.Message); }
        }
        copy.Click += (_, _) => Finish(DialogResult.OK); rename.Click += (_, _) => Finish(DialogResult.Yes);
        name.KeyPress += (_, _) => UiSounds.Play(UiCue.Tick);
        content.Click += (_, _) => UiSounds.Play(UiCue.Select); worlds.Click += (_, _) => UiSounds.Play(UiCue.Select);
        Controls.AddRange([title, name, content, worlds, copy, rename, close]);
        AcceptButton = copy; CancelButton = close;
        Shown += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}

internal sealed class SkinModelDialog : Form
{
    internal bool Slim { get; private set; }
    internal SkinModelDialog()
    {
        Text = L.T("Модель скина"); BackColor = PixelTheme.Background; ForeColor = PixelTheme.Text; Font = PixelTheme.Font();
        FormBorderStyle = FormBorderStyle.FixedDialog; ClientSize = new Size(430, 165);
        MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
        var title = PixelTheme.TextLabel(L.T("Модель скина"), 18); title.Location = new Point(24, 25);
        var classic = new PixelButton { Text = L.T("Классическая"), Primary = true, Bounds = new Rectangle(24, 82, 185, 46) };
        var slim = new PixelButton { Text = L.T("Тонкие руки"), Bounds = new Rectangle(221, 82, 185, 46) };
        classic.Click += (_, _) => { Slim = false; DialogResult = DialogResult.OK; Close(); };
        slim.Click += (_, _) => { Slim = true; DialogResult = DialogResult.OK; Close(); };
        Controls.AddRange([title, classic, slim]); AcceptButton = classic;
    }
}
