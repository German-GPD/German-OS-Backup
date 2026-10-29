using System.ComponentModel;

namespace GermanOSBackup;

internal sealed class RestoreForm : Form
{
    private readonly string backupRoot;
    private readonly TextBox search = new() { PlaceholderText = "Поиск по имени файла или пути…", Dock = DockStyle.Fill };
    private readonly ListView files = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = true,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly TextBox destination = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button restore = Palette.Button("Восстановить выбранные", true);
    private readonly Button refresh = Palette.Button("Обновить список", false);
    private readonly Button selectAll = Palette.Button("Выбрать всё", false);
    private readonly Button selectNone = Palette.Button("Снять выбор", false);
    private readonly Button chooseDestination = Palette.Button("Папка восстановления…", false);
    private List<string> allFiles = new();
    private readonly HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool loading;

    public RestoreForm(string backupRoot)
    {
        this.backupRoot = Path.GetFullPath(backupRoot);
        Text = "Восстановление файлов · German OS Backup";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 650);
        MinimumSize = new Size(680, 460);
        BackColor = Palette.Background;
        ForeColor = Palette.Text;
        Font = Palette.Font;

        files.Columns.Add("Файл резервной копии", 560);
        files.Columns.Add("Размер", 120);
        files.Columns.Add("Изменён", 150);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(18),
            BackColor = Palette.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var header = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Background };
        var title = Palette.Label("Восстановление из резервной копии", 19, Palette.Text, true);
        title.SetBounds(0, 0, 800, 30);
        header.Controls.Add(title);
        var subtitle = Palette.Label("Отметь файлы, укажи папку назначения. Исходные данные не изменяются.", 10, Palette.Muted);
        subtitle.SetBounds(1, 34, 800, 22);
        header.Controls.Add(subtitle);

        var listPanel = new Panel { Dock = DockStyle.Fill, BackColor = Palette.Panel, Padding = new Padding(8) };
        var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Palette.Panel };
        listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listLayout.Controls.Add(search, 0, 0);
        listLayout.Controls.Add(files, 0, 1);
        listPanel.Controls.Add(listLayout);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Palette.Background };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        bottom.Controls.Add(destination, 0, 0);
        bottom.Controls.Add(chooseDestination, 1, 0);
        bottom.Controls.Add(status, 0, 1);
        bottom.Controls.Add(restore, 1, 1);
        chooseDestination.Dock = DockStyle.Fill;
        restore.Dock = DockStyle.Fill;

        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        foreach (var button in new[] { refresh, selectAll, selectNone }) footer.Controls.Add(button);
        refresh.Width = 160; selectAll.Width = 130; selectNone.Width = 130;

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(listPanel, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);

        files.BackColor = Palette.Panel;
        files.ForeColor = Palette.Text;
        search.BackColor = Palette.Panel;
        search.ForeColor = Palette.Text;
        destination.BackColor = Palette.Background;
        destination.ForeColor = Palette.Muted;
        status.ForeColor = Palette.Muted;

        chooseDestination.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Выбери папку, в которую нужно восстановить файлы",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                destination.Text = Path.GetFullPath(dialog.SelectedPath);
                UpdateStatus();
            }
        };
        refresh.Click += async (_, _) => await LoadFilesAsync();
        search.TextChanged += (_, _) => ApplyFilter();
        selectAll.Text = "Выбрать показанные";
        selectAll.Click += (_, _) =>
        {
            foreach (ListViewItem item in files.Items)
            {
                if (item.Tag is string path) checkedPaths.Add(path);
                item.Checked = true;
            }
            UpdateStatus();
        };
        selectNone.Click += (_, _) =>
        {
            checkedPaths.Clear();
            loading = true;
            foreach (ListViewItem item in files.Items) item.Checked = false;
            loading = false;
            UpdateStatus();
        };
        files.ItemChecked += (_, e) =>
        {
            if (loading || e.Item.Tag is not string path) return;
            if (e.Item.Checked) checkedPaths.Add(path);
            else checkedPaths.Remove(path);
            UpdateStatus();
        };
        restore.Click += async (_, _) => await RestoreSelectedAsync();

        destination.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Shown += async (_, _) => await LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        if (!Directory.Exists(backupRoot))
        {
            MessageBox.Show(this, "Папка резервной копии не найдена:\n" + backupRoot, "Восстановление", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        refresh.Enabled = false;
        restore.Enabled = false;
        status.Text = "Сканируем резервную копию…";
        try
        {
            allFiles = await Task.Run(() => EnumerateBackupFilesSafely(backupRoot)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return !name.Contains(".gob-tmp-", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains(".gob-rust-", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".gob-restore-tmp", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList());
            checkedPaths.IntersectWith(allFiles);
            ApplyFilter();
            status.Text = $"Найдено файлов: {allFiles.Count:N0}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось прочитать резервную копию:\n" + ex.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            refresh.Enabled = true;
            UpdateStatus();
        }
    }

    private void ApplyFilter()
    {
        var query = search.Text.Trim();
        loading = true;
        files.BeginUpdate();
        try
        {
            files.Items.Clear();
            IEnumerable<string> filtered = allFiles;
            if (query.Length > 0)
                filtered = filtered.Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase));
            // Keep the UI responsive for large game-save/AppData backups.
            // Search still works across the complete allFiles collection.
            const int displayLimit = 1500;
            foreach (var path in filtered.Take(displayLimit))
            {
                try
                {
                    var info = new FileInfo(path);
                    var relative = Path.GetRelativePath(backupRoot, path);
                    var item = new ListViewItem(relative) { Tag = path, Checked = checkedPaths.Contains(path) };
                    item.SubItems.Add(FormatSize(info.Length));
                    item.SubItems.Add(info.LastWriteTime.ToString("dd.MM.yyyy HH:mm"));
                    files.Items.Add(item);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        finally
        {
            files.EndUpdate();
            loading = false;
        }
        UpdateStatus();
        var matchingCount = query.Length == 0 ? allFiles.Count : allFiles.Count(path =>
            path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase));
        if (matchingCount > 1500)
            status.Text = $"Найдено: {matchingCount:N0} · показаны первые 1 500 — уточни поиск";
    }

    private static List<string> EnumerateBackupFilesSafely(string root)
    {
        var result = new List<string>();
        var pendingDirs = new Stack<string>();
        pendingDirs.Push(root);
        while (pendingDirs.Count > 0)
        {
            var current = pendingDirs.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                            result.Add(file);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        // Never follow junctions/symlinks: they can loop or point outside the backup.
                        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0)
                            pendingDirs.Push(dir);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private async Task RestoreSelectedAsync()
    {
        var selected = checkedPaths.Where(File.Exists).ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Отметь хотя бы один файл для восстановления.", "Восстановление", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(destination.Text))
        {
            MessageBox.Show(this, "Сначала выбери папку восстановления.", "Восстановление", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var targetRoot = Path.GetFullPath(destination.Text);
        if (IsSameOrChild(targetRoot, backupRoot) || IsSameOrChild(backupRoot, targetRoot))
        {
            MessageBox.Show(this, "Папка восстановления не должна совпадать с папкой резервной копии, находиться внутри неё или быть её родительской папкой.", "Небезопасный путь", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var existingCount = selected.Count(source =>
        {
            var target = Path.GetFullPath(Path.Combine(targetRoot, Path.GetRelativePath(backupRoot, source)));
            return File.Exists(target);
        });
        var warning = $"Выбрано файлов: {selected.Count:N0}.";
        if (existingCount > 0) warning += $"\nУже существуют и будут пропущены: {existingCount:N0}.";
        warning += "\n\nСуществующие файлы не будут перезаписаны. Продолжить?";
        if (MessageBox.Show(this, warning, "Подтверди восстановление", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        restore.Enabled = false;
        refresh.Enabled = false;
        var copied = 0;
        var skipped = 0;
        var errors = new List<string>();
        var progress = new Progress<(int Done, int Total, string Current)>(value =>
            status.Text = $"Восстановлено {value.Done:N0} из {value.Total:N0}: {value.Current}");
        try
        {
            await Task.Run(() =>
            {
                for (var index = 0; index < selected.Count; index++)
                {
                    var source = selected[index];
                    try
                    {
                        var relative = Path.GetRelativePath(backupRoot, source);
                        var target = Path.GetFullPath(Path.Combine(targetRoot, relative));
                        if (!IsSameOrChild(target, targetRoot))
                            throw new InvalidDataException("Небезопасный путь файла в резервной копии.");
                        if (File.Exists(target)) { Interlocked.Increment(ref skipped); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        var temp = target + ".gob-restore-tmp";
                        try
                        {
                            File.Copy(source, temp, true);
                            File.Move(temp, target, false);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                        copied++;
                    }
                    catch (Exception ex) { errors.Add(Path.GetRelativePath(backupRoot, source) + ": " + ex.Message); }
                    ((IProgress<(int Done, int Total, string Current)>)progress).Report((index + 1, selected.Count, Path.GetFileName(source)));
                }
            });
        }
        finally
        {
            restore.Enabled = true;
            refresh.Enabled = true;
        }

        var summary = $"Восстановлено: {copied:N0} из {selected.Count:N0}.";
        if (skipped > 0) summary += $"\nПропущено существующих файлов: {skipped:N0}.";
        if (errors.Count > 0)
            summary += $"\nОшибок: {errors.Count:N0}.\n\n" + string.Join("\n", errors.Take(8));
        MessageBox.Show(this, summary, errors.Count == 0 ? "Восстановление завершено" : "Восстановление завершено с ошибками",
            MessageBoxButtons.OK, errors.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        status.Text = summary.Split('\n')[0];
    }

    private void UpdateStatus()
    {
        var checkedCount = checkedPaths.Count;
        var hasDestination = !string.IsNullOrWhiteSpace(destination.Text) && Directory.Exists(destination.Text);
        restore.Enabled = !loading && allFiles.Count > 0 && hasDestination;
        if (allFiles.Count == 0)
            status.Text = "Резервная копия пуста — сначала создай копию файлов.";
        else if (!hasDestination)
            status.Text = $"Файлов: {files.Items.Count:N0} · выбери папку назначения";
        else
            status.Text = $"Файлов: {files.Items.Count:N0} · выбрано: {checkedCount:N0}";
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }
}
