namespace GermanOSBackup;

internal sealed class FirstRunForm : Form
{
    private readonly TextBox path = new() { ReadOnly = true, BackColor = Palette.Panel, ForeColor = Palette.Text, BorderStyle = BorderStyle.FixedSingle };
    public string? Destination => path.Text;
    public bool StartWithWindows => startWithWindows.Checked;
    private readonly CheckBox startWithWindows = new() { Text = "Запускать вместе с Windows и работать в фоне", Checked = true, AutoSize = true, ForeColor = Palette.Text, BackColor = Palette.Background };

    public FirstRunForm()
    {
        Text = "Первый запуск · German OS Backup C# Edition"; ClientSize = new Size(650, 490);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; BackColor = Palette.Background; ForeColor = Palette.Text; Font = Palette.Font;
        var badge = Palette.Label("ПЕРВЫЙ ЗАПУСК", 10, Palette.Green, true); badge.SetBounds(30, 25, 250, 22); Controls.Add(badge);
        var title = Palette.Label("Сначала — большая копия.", 24, Palette.Text, true); title.SetBounds(30, 55, 590, 42); Controls.Add(title);
        var description = Palette.Label("Первичное копирование может занять часы. На первом проходе программа временно использует несколько потоков копирования, чтобы быстрее сохранить доступные файлы.", 11, Palette.Muted);
        description.SetBounds(32, 108, 580, 58); Controls.Add(description);
        var warning = new Panel { BackColor = Palette.Panel, BorderStyle = BorderStyle.FixedSingle }; warning.SetBounds(30, 183, 590, 90); Controls.Add(warning);
        var warningTitle = Palette.Label("Перед началом", 12, Palette.Green, true); warningTitle.SetBounds(16, 12, 500, 22); warning.Controls.Add(warningTitle);
        var warningText = Palette.Label("Выбери другой диск с достаточным свободным местом. Первый проход может заметно нагружать диск и процессор. Не отключай компьютер и не извлекай диск.", 10, Palette.Muted);
        warningText.SetBounds(16, 39, 555, 42); warning.Controls.Add(warningText);
        var choose = Palette.Button("Выбрать папку…", false); choose.SetBounds(30, 295, 150, 40); Controls.Add(choose);
        path.SetBounds(192, 301, 428, 30); Controls.Add(path);
        choose.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Выбери папку на другом диске", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { Program.ValidateDestination(dialog.SelectedPath); path.Text = dialog.SelectedPath; }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Неподходящая папка", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        startWithWindows.SetBounds(32, 355, 570, 26); Controls.Add(startWithWindows);
        var startupHint = Palette.Label("Можно изменить позже в настройках приложения.", 9, Palette.Muted); startupHint.SetBounds(32, 382, 570, 20); Controls.Add(startupHint);
        var start = Palette.Button("Продолжить настройку  →", true); start.SetBounds(330, 425, 290, 42); Controls.Add(start);
        start.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(path.Text)) { MessageBox.Show(this, "Сначала выбери папку на другом диске."); return; }
            try { Program.ValidateDestination(path.Text); DialogResult = DialogResult.OK; Close(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверь путь", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        AcceptButton = start;
    }
}