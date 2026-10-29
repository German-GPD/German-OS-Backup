namespace GermanOSBackup;

internal sealed class SourcesForm : Form
{
    private readonly TreeView tree = new() { CheckBoxes = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false };
    private readonly AppConfig config;
    private bool loading;

    public SourcesForm(AppConfig config)
    {
        this.config = config; Text = "Источники резервного копирования"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 600); MinimumSize = new Size(560, 420); BackColor = Palette.Background; ForeColor = Palette.Text; Font = Palette.Font;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(18), BackColor = Palette.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background };
        var title = Palette.Label("Выбери папки для копирования", 19, Palette.Text, true); title.SetBounds(0, 0, 650, 31); header.Controls.Add(title);
        var subtitle = Palette.Label("Отметь папки галочками. Можно выбрать весь раздел или добавить любую папку вручную.", 10, Palette.Muted); subtitle.SetBounds(1, 35, 650, 26); header.Controls.Add(subtitle);
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Panel, Padding = new Padding(10) }; tree.BackColor = Palette.Panel; tree.ForeColor = Palette.Text; tree.ShowNodeToolTips = true; host.Controls.Add(tree);
        var tips = new ToolTip { AutoPopDelay = 10000, InitialDelay = 350, ReshowDelay = 150, ShowAlways = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = Palette.Button("Сохранить выбор", true); save.Width = 150; var cancel = Palette.Button("Отмена", false); cancel.Width = 100; var add = Palette.Button("Добавить папку…", false); add.Width = 145; var clear = Palette.Button("Снять все", false); clear.Width = 95;
        bottom.Controls.Add(save); bottom.Controls.Add(cancel); bottom.Controls.Add(add); bottom.Controls.Add(clear); layout.Controls.Add(header, 0, 0); layout.Controls.Add(host, 0, 1); layout.Controls.Add(bottom, 0, 2); Controls.Add(layout);
        tips.SetToolTip(tree, "Отметь папки, содержимое которых нужно сохранять.");
        tips.SetToolTip(add, "Добавить папку, которой нет в списке стандартных источников.");
        tips.SetToolTip(clear, "Снять выделение со всех папок, чтобы начать выбор заново.");
        tips.SetToolTip(save, "Сохранить выбранные папки и вернуться в главное окно.");
        tips.SetToolTip(cancel, "Отменить изменения и оставить прежний выбор.");
        BuildTree(); tree.BeforeExpand += (_, e) => LoadChildren(e.Node!); tree.AfterCheck += (_, e) => { if (!loading) SetChildren(e.Node!, e.Node!.Checked); };
        clear.Click += (_, _) => { loading = true; foreach (TreeNode node in tree.Nodes) { node.Checked = false; SetChildren(node, false); } loading = false; };
        add.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Выбери папку для резервного копирования", ShowNewFolderButton = false }; if (d.ShowDialog(this) == DialogResult.OK && Directory.Exists(d.SelectedPath)) AddPath(d.SelectedPath, true); };
        save.Click += (_, _) =>
        {
            var selected = GetSelectedRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Выбери хотя бы одну папку для резервного копирования.", "German OS Backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var destination = Path.GetFullPath(config.Destination).TrimEnd(Path.DirectorySeparatorChar);
                foreach (var source in selected)
                {
                    var sourceFull = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
                    if (string.Equals(destination, sourceFull, StringComparison.OrdinalIgnoreCase) ||
                        destination.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        sourceFull.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this,
                            $"Папка копии не должна совпадать с выбранным источником, находиться внутри него или быть его родительской папкой.\\n\\nИсточник: {sourceFull}\\nПапка копии: {destination}",
                            "Небезопасный путь", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }

                config.SelectedSources = selected;
                Program.SaveConfig(config);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Не удалось сохранить выбор папок: {ex.Message}", "German OS Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
    }

    private void BuildTree()
    {
        var selected = config.SelectedSources is { Count: > 0 } ? config.SelectedSources : BackupEngine.DefaultSources().ToList();
        foreach (var path in BackupEngine.Sources().Select(x => x.Path)) if (Directory.Exists(path)) AddPath(path, selected.Any(s => string.Equals(Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)));
        foreach (var path in selected) if (Directory.Exists(path) && !tree.Nodes.Cast<TreeNode>().Any(n => IsSameOrChild(n.Tag?.ToString() ?? "", path))) AddPath(path, true);
    }
    private void AddPath(string path, bool checkedState)
    {
        var root = Path.GetPathRoot(path) ?? path; var parts = path.Substring(Math.Min(root.Length, path.Length)).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        TreeNode? parent = null; string current = root;
        if (parts.Length == 0) { var node = new TreeNode(path) { Tag = path, Checked = checkedState, ToolTipText = "Сохранять файлы из этой папки" }; node.Nodes.Add(new TreeNode("…")); tree.Nodes.Add(node); return; }
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var node = (parent == null ? tree.Nodes : parent.Nodes).Cast<TreeNode>().FirstOrDefault(n => string.Equals(n.Tag?.ToString(), current, StringComparison.OrdinalIgnoreCase));
            if (node == null) { node = new TreeNode(part) { Tag = current, ToolTipText = current }; try { if (Directory.GetDirectories(current).Length > 0) node.Nodes.Add(new TreeNode("…")); } catch { } if (parent == null) tree.Nodes.Add(node); else parent.Nodes.Add(node); }
            parent = node;
        }
        if (parent != null) { parent.Checked = checkedState; parent.Expand(); }
    }
    private void LoadChildren(TreeNode node)
    {
        var path = node.Tag?.ToString(); if (path == null || (node.Nodes.Count == 1 && node.Nodes[0].Text != "…")) return;
        node.Nodes.Clear(); try { foreach (var dir in Directory.GetDirectories(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) { var child = new TreeNode(Path.GetFileName(dir)) { Tag = dir, ToolTipText = dir }; try { if (Directory.GetDirectories(dir).Length > 0) child.Nodes.Add(new TreeNode("…")); } catch { } node.Nodes.Add(child); } } catch { }
        loading = true; foreach (TreeNode child in node.Nodes) child.Checked = config.SelectedSources?.Any(s => string.Equals(s, child.Tag?.ToString(), StringComparison.OrdinalIgnoreCase) || IsSameOrChild(s, child.Tag?.ToString() ?? "")) == true; loading = false;
    }
    private void SetChildren(TreeNode node, bool value) { loading = true; foreach (TreeNode child in node.Nodes) { if (child.Text == "…") continue; child.Checked = value; SetChildren(child, value); } loading = false; }
    private IEnumerable<string> GetSelectedRoots() { foreach (TreeNode node in tree.Nodes) foreach (var p in Selected(node, false)) yield return p; }
    private IEnumerable<string> Selected(TreeNode node, bool ancestorChecked)
    {
        var path = node.Tag?.ToString(); if (node.Checked && !ancestorChecked && path != null) { yield return path; yield break; }
        foreach (TreeNode child in node.Nodes) foreach (var p in Selected(child, ancestorChecked || node.Checked)) yield return p;
    }
    private static bool IsSameOrChild(string parent, string child) => string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), child.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
