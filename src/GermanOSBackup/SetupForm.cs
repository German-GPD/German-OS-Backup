namespace GermanOSBackup;

internal sealed class SetupForm : Form
{
    private readonly TextBox destination = new() { ReadOnly = true, Width = 350 };
    public string? Destination => destination.Text;

    public SetupForm()
    {
        Text = "German OS Backup — настройка";
        Width = 560; Height = 255; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        BackColor = Color.FromArgb(16, 19, 16); ForeColor = Color.FromArgb(237, 242, 237);
        Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "Куда сохранять резервные копии?", Font = new Font("Segoe UI", 16, FontStyle.Bold), AutoSize = true, Left = 22, Top = 18 });
        Controls.Add(new Label { Text = "Выбери папку на диске, отличном от системного C:. В ней появятся Users, ProgramData и XboxGames.", Left = 24, Top = 60, Width = 495, Height = 42, ForeColor = Color.Silver });
        destination.SetBounds(24, 112, 350, 32); Controls.Add(destination);
        var browse = new Button { Text = "Обзор…", Left = 388, Top = 110, Width = 120, Height = 34 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Папка для резервных копий", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.Equals(Path.GetPathRoot(dialog.SelectedPath), systemDrive, StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Выбери другой диск, не системный C:.", "German OS Backup"); return; }
            destination.Text = dialog.SelectedPath;
        };
        Controls.Add(browse);
        Controls.Add(new Label { Text = "Копии не удаляются, если оригиналы удалены.", Left = 24, Top = 153, Width = 480, ForeColor = Color.FromArgb(154, 242, 173) });
        var start = new Button { Text = "Начать резервное копирование", Left = 282, Top = 184, Width = 226, Height = 34 };
        start.Click += (_, _) => { if (string.IsNullOrWhiteSpace(destination.Text)) { MessageBox.Show(this, "Сначала выбери папку."); return; } DialogResult = DialogResult.OK; Close(); };
        Controls.Add(start);
        AcceptButton = start;
    }
}
