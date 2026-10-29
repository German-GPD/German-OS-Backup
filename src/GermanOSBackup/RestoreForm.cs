namespace GermanOSBackup;

internal sealed class RestoreForm : Form
{
    private readonly TextBox backupPath = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox targetPath = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label summary = Palette.Label("Выбери папку резервной копии и место восстановления.", 10, Palette.Muted);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100 };
    private readonly Button restore = Palette.Button("Восстановить отсутствующие файлы", true);
    private readonly CancellationTokenSource cancel = new();
    private bool running;

    public RestoreForm(string suggestedBackup)
    {
        Text = "Восстановление из резервной копии";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 330); MinimumSize = new Size(650, 300);
        BackColor = Palette.Background; ForeColor = Palette.Text; Font = Palette.Font;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 5, BackColor = Palette.Background };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 10));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var title = Palette.Label("Верни файлы из локальной копии", 18, Palette.Text, true);
        layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 3);
        var pickBackup = Palette.Button("Выбрать копию…", false);
        backupPath.Text = Directory.Exists(suggestedBackup) ? suggestedBackup : "";
        layout.Controls.Add(backupPath, 0, 1); layout.Controls.Add(pickBackup, 1, 1);
        var pickTarget = Palette.Button("Куда вернуть…", false);
        layout.Controls.Add(targetPath, 0, 2); layout.Controls.Add(pickTarget, 1, 2);
        layout.Controls.Add(summary, 0, 3); layout.SetColumnSpan(summary, 3);
        layout.Controls.Add(progress, 0, 4); layout.SetColumnSpan(progress, 3);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 0, 0) };
        restore.Width = 250; var close = Palette.Button("Закрыть", false); close.Width = 110;
        footer.Controls.Add(close); footer.Controls.Add(restore);
        Controls.Add(layout); Controls.Add(footer);
        pickBackup.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Выбери корневую папку существующего бэкапа", ShowNewFolderButton = false }; if (d.ShowDialog(this) == DialogResult.OK) backupPath.Text = d.SelectedPath; };
        pickTarget.Click += (_, _) => { using var d = new FolderBrowserDialog { Description = "Выбери папку, куда восстановить файлы", ShowNewFolderButton = true }; if (d.ShowDialog(this) == DialogResult.OK) targetPath.Text = d.SelectedPath; };
        close.Click += (_, _) => { if (running) cancel.Cancel(); else Close(); };
        restore.Click += async (_, _) => await RestoreAsync();
        FormClosing += (_, e) => { if (running) { cancel.Cancel(); e.Cancel = true; } };
    }

    private async Task RestoreAsync()
    {
        var source = backupPath.Text.Trim(); var target = targetPath.Text.Trim();
        if (!Directory.Exists(source) || string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "Выбери существующую папку копии и папку назначения.", "Восстановление", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        source = NormalizeDirectoryPath(source);
        target = NormalizeDirectoryPath(target);
        if (IsSameOrNested(source, target) || IsSameOrNested(target, source))
        {
            MessageBox.Show(this, "Папка источника и папка назначения не должны пересекаться.", "Небезопасный путь", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (MessageBox.Show(this, "Будут восстановлены только отсутствующие файлы. Существующие файлы не перезаписываются. Это не восстановление Windows целиком. Продолжить?", "Подтверждение восстановления", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        running = true; restore.Enabled = false; progress.Value = 0;
        long restored = 0, skipped = 0, errors = 0;
        try
        {
            await Task.Run(() =>
            {
                var files = EnumerateFilesSafely(source, cancel.Token, ex =>
                {
                    Interlocked.Increment(ref errors);
                    try { File.AppendAllText(Program.LogPath, $"[{DateTime.Now:O}] RESTORE ENUMERATION: {ex}{Environment.NewLine}"); } catch { }
                });
                var total = Math.Max(1, files.Count); int done = 0;
                foreach (var file in files)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(source, file);
                    var destination = ResolveRestoreDestination(target, relative);
                    if (!IsSameOrNested(destination, target) || string.Equals(destination, target, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref errors);
                        continue;
                    }
                    try
                    {
                        if (File.Exists(destination)) Interlocked.Increment(ref skipped);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            var temp = destination + ".gob-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                File.Copy(file, temp, false);
                                File.Move(temp, destination, false);
                                Interlocked.Increment(ref restored);
                            }
                            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        try { File.AppendAllText(Program.LogPath, $"[{DateTime.Now:O}] RESTORE {file}: {ex}{Environment.NewLine}"); } catch { }
                    }
                    done++;
                    if (done % 20 == 0 || done == total)
                    {
                        var value = Math.Min(100, (int)(done * 100L / total));
                        if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(() => progress.Value = value));
                    }
                }
            }, cancel.Token);
            summary.Text = $"Готово. Восстановлено: {restored:N0}; пропущено существующих: {skipped:N0}; ошибок: {errors:N0}.";
            MessageBox.Show(this, summary.Text, "Восстановление завершено", MessageBoxButtons.OK, errors == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { summary.Text = $"Остановлено. Восстановлено: {restored:N0}; пропущено: {skipped:N0}; ошибок: {errors:N0}."; }
        catch (Exception ex) { summary.Text = "Не удалось выполнить восстановление."; MessageBox.Show(this, ex.Message, "Ошибка восстановления", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { running = false; restore.Enabled = true; }
    }

    private static string ResolveRestoreDestination(string target, string relative)
    {
        var normalizedTarget = NormalizeDirectoryPath(target);
        var root = Path.GetPathRoot(normalizedTarget);
        // When a drive root is selected, map standard backup roots back to the
        // actual Windows Known Folder. This avoids creating C:\\Изображения, etc.
        if (string.Equals(normalizedTarget, root, StringComparison.OrdinalIgnoreCase))
        {
            var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, 2,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                var knownFolder = parts[0].ToLowerInvariant() switch
                {
                    "рабочий стол" or "desktop" => Environment.SpecialFolder.DesktopDirectory,
                    "документы" or "documents" => Environment.SpecialFolder.MyDocuments,
                    "изображения" or "pictures" or "рисунки" => Environment.SpecialFolder.MyPictures,
                    "видео" or "videos" => Environment.SpecialFolder.MyVideos,
                    "загрузки" or "downloads" => (Environment.SpecialFolder?)null,
                    _ => (Environment.SpecialFolder?)null
                };
                var basePath = knownFolder.HasValue
                    ? Environment.GetFolderPath(knownFolder.Value)
                    : parts[0].Equals("загрузки", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("downloads", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                        : "";
                if (!string.IsNullOrWhiteSpace(basePath))
                {
                    var remainder = Path.GetRelativePath(parts[0], relative);
                    return Path.GetFullPath(Path.Combine(basePath, remainder == "." ? "" : remainder));
                }
            }
        }
        return Path.GetFullPath(Path.Combine(target, relative));
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSameOrNested(string candidate, string parent)
    {
        candidate = NormalizeDirectoryPath(candidate);
        parent = NormalizeDirectoryPath(parent);
        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> EnumerateFilesSafely(string root, CancellationToken token, Action<Exception> onError)
    {
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    token.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }
            catch (Exception ex) { onError(ex); }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                            stack.Push(directory);
                    }
                    catch (Exception ex) { onError(ex); }
                }
            }
            catch (Exception ex) { onError(ex); }
        }
        return files;
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { cancel.Cancel(); cancel.Dispose(); base.OnFormClosed(e); }
}
