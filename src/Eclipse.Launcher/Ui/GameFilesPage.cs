using System.Diagnostics;

namespace Eclipse;

internal sealed class GameFilesPage : UserControl
{
    private readonly string root;
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false, ShowLines = false, ShowPlusMinus = true, FullRowSelect = true, ItemHeight = 29, Indent = 22 };
    private readonly TextBox editor = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsTab = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, MaxLength = 4 * 1024 * 1024 };
    private readonly Label filename = PixelTheme.TextLabel(L.T("Выберите файл"));
    private readonly PixelButton save = new() { Text = L.T("Сохранить"), Width = 150, Dock = DockStyle.Right, Enabled = false };
    private readonly Action<string> report;
    private EditableGameFile? document;
    private bool dirty;
    private bool binding;
    private bool opening;
    public GameFilesPage(AppPaths paths, Action<string> report)
    {
        Size = new Size(1000, 540);
        root = paths.Game; this.report = report;
        PixelTheme.Style(tree); PixelTheme.Style(editor); BackColor = PixelTheme.Background;
        editor.BackColor = Color.FromArgb(19, 23, 20); tree.BackColor = PixelTheme.Panel;
        editor.Enabled = false;
        var split = new SplitContainer { Size = new Size(900, 500), Dock = DockStyle.Fill, SplitterWidth = 3, BackColor = PixelTheme.Line, Panel1MinSize = 170, Panel2MinSize = 250 };
        split.Panel1.Controls.Add(tree);
        var filebar = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 4, 4, 4), BackColor = PixelTheme.Panel };
        filename.AutoSize = false; filename.Dock = DockStyle.Fill; filename.TextAlign = ContentAlignment.MiddleLeft;
        filebar.Controls.Add(filename); filebar.Controls.Add(save);
        split.Panel2.Controls.Add(editor); split.Panel2.Controls.Add(filebar);
        var bar = new Panel { Dock = DockStyle.Top, Height = 58, Padding = new Padding(8) };
        var title = PixelTheme.TextLabel(L.T("ФАЙЛЫ MINECRAFT"), 16); title.Location = new Point(24, 18);
        var refresh = new PixelButton { Text = L.T("Обновить"), Dock = DockStyle.Right, Width = 145 };
        var folder = new PixelButton { Text = L.T("Папка"), Dock = DockStyle.Right, Width = 110 };
        var mods = new PixelButton { Text = L.T("Моды"), Dock = DockStyle.Right, Width = 100 };
        mods.Click += (_, _) => { var directory = Path.Combine(root, "mods"); Directory.CreateDirectory(directory); Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true }); };
        bar.Controls.Add(title); bar.Controls.Add(mods); bar.Controls.Add(folder); bar.Controls.Add(refresh);
        Controls.Add(split); Controls.Add(bar);
        Load += (_, _) => split.SplitterDistance = Math.Clamp(split.Width / 4, split.Panel1MinSize, Math.Max(split.Panel1MinSize, split.Width - split.Panel2MinSize - split.SplitterWidth));
        refresh.Click += async (_, _) => await ReloadAsync();
        folder.Click += (_, _) => { Directory.CreateDirectory(root); Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); };
        tree.BeforeExpand += async (_, e) => { if (e.Node is { } node && node.Nodes.Count == 1 && node.Nodes[0].Tag == null) await PopulateAsync(node); };
        tree.AfterSelect += (_, e) => { if (e.Action is TreeViewAction.ByMouse or TreeViewAction.ByKeyboard) UiSounds.Play(UiCue.Select); };
        tree.AfterExpand += (_, _) => { if (tree.Focused) UiSounds.Play(UiCue.Select); };
        tree.AfterCollapse += (_, _) => { if (tree.Focused) UiSounds.Play(UiCue.Back); };
        tree.AfterSelect += async (_, e) =>
        {
            if (e.Node?.Tag is not string path || Directory.Exists(path) || opening) return;
            if (!ConfirmLeave()) return;
            opening = true;
            try
            {
                var loaded = await Task.Run(() => GameFiles.Read(root, path));
                if (IsDisposed) return;
                binding = true; document = loaded; editor.Enabled = true; editor.Text = loaded.Text;
                L.PreserveUserText(filename);
                filename.Text = Path.GetRelativePath(root, path); dirty = false; save.Enabled = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report(ex.Message); }
            finally { binding = false; opening = false; }
        };
        editor.TextChanged += (_, _) => { if (!binding && document != null) { dirty = true; save.Enabled = true; } };
        editor.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.S) { UiSounds.Play(UiCue.Confirm); Save(); e.SuppressKeyPress = true; } };
        editor.KeyPress += (_, _) => UiSounds.Play(UiCue.Tick);
        save.Click += (_, _) => Save();
    }
    public bool ConfirmLeave()
    {
        if (!dirty) return true;
        var answer = PixelDialog.Ask(FindForm(), L.T("Сохранить изменения?"), filename.Text);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes) { Save(); return !dirty; }
        return true;
    }
    private void Save()
    {
        if (document == null || !dirty) return;
        try { document = GameFiles.Save(root, document, editor.Text); dirty = false; save.Enabled = false; report(L.T("Файл сохранён")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report(ex.Message); }
    }
    public async Task ReloadAsync()
    {
        if (!ConfirmLeave()) return;
        dirty = false; save.Enabled = false; editor.Clear(); document = null; editor.Enabled = false; L.SetText(filename, "Выберите файл");
        Directory.CreateDirectory(root);
        tree.Nodes.Clear();
        var node = new TreeNode("minecraft") { Tag = root };
        tree.Nodes.Add(node);
        await PopulateAsync(node);
        node.Expand();
    }
    private async Task PopulateAsync(TreeNode node)
    {
        if (node.Tag is not string path) return;
        try
        {
            var entries = await Task.Run(() => new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f is DirectoryInfo).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            if (IsDisposed) return;
            tree.BeginUpdate(); node.Nodes.Clear();
            foreach (var entry in entries)
            {
                var child = new TreeNode((entry is DirectoryInfo ? "+ " : "  ") + entry.Name) { Tag = entry.FullName };
                if (entry is DirectoryInfo) child.Nodes.Add(new TreeNode("…"));
                node.Nodes.Add(child);
            }
            tree.EndUpdate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report(ex.Message); }
    }
}

internal sealed class PixelDialog : Form
{
    private PixelDialog(string title, string message, bool ask)
    {
        Text = title; Font = PixelTheme.Font(); BackColor = PixelTheme.Background; ForeColor = PixelTheme.Text;
        var tall = !ask && message.Length > 260;
        ClientSize = new Size(620, tall ? 460 : 230); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
        var label = new Label { Text = message, Font = PixelTheme.Font(), ForeColor = PixelTheme.Text, Bounds = new Rectangle(24, 24, 572, tall ? 348 : 132) };
        Controls.Add(label);
        var ok = new PixelButton { Text = ask ? L.T("Сохранить") : L.T("Закрыть"), Bounds = new Rectangle(440, tall ? 396 : 172, 150, 42), Primary = true, DialogResult = ask ? DialogResult.Yes : DialogResult.OK };
        Controls.Add(ok); AcceptButton = ok;
        if (ask)
        {
            var no = new PixelButton { Text = L.T("Не сохранять"), Bounds = new Rectangle(260, 172, 170, 42), DialogResult = DialogResult.No };
            var cancel = new PixelButton { Text = L.T("Отмена"), Bounds = new Rectangle(100, 172, 150, 42), DialogResult = DialogResult.Cancel };
            Controls.Add(no); Controls.Add(cancel); CancelButton = cancel;
        }
        else CancelButton = ok;
    }
    public static DialogResult Ask(IWin32Window? owner, string title, string message) { using var dialog = new PixelDialog(title, message, true); return dialog.ShowDialog(owner); }
    public static void Message(IWin32Window? owner, string title, string message) { using var dialog = new PixelDialog(title, message, false); dialog.ShowDialog(owner); }
}
