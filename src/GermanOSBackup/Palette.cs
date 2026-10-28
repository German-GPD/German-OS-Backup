namespace GermanOSBackup;

internal static class Palette
{
    public static readonly Color Background = Color.FromArgb(15, 18, 16);
    public static readonly Color Panel = Color.FromArgb(23, 28, 24);
    public static readonly Color Panel2 = Color.FromArgb(29, 36, 30);
    public static readonly Color Line = Color.FromArgb(49, 60, 51);
    public static readonly Color Text = Color.FromArgb(239, 244, 239);
    public static readonly Color Muted = Color.FromArgb(157, 170, 159);
    public static readonly Color Green = Color.FromArgb(151, 240, 171);
    public static readonly Font Font = new("Segoe UI", 10F, FontStyle.Regular);
    public static Label Label(string text, float size, Color color, bool bold = false) => new() { Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, AutoSize = false, UseCompatibleTextRendering = true };
    public static Button Button(string text, bool primary)
    {
        var b = new Button { Text = text, Font = new Font("Segoe UI", 10F, FontStyle.Bold), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, ForeColor = primary ? Background : Text, BackColor = primary ? Green : Panel2, UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderColor = primary ? Green : Line; b.FlatAppearance.BorderSize = 1; b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(181, 255, 195) : Color.FromArgb(39, 48, 40);
        return b;
    }
}