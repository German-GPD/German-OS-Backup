namespace GermanOSBackup;

internal static class Palette
{
    internal static readonly Color Background = Color.FromArgb(5, 6, 5);
    internal static readonly Color Panel = Color.FromArgb(9, 12, 10);
    internal static readonly Color Text = Color.FromArgb(237, 248, 239);
    internal static readonly Color Muted = Color.FromArgb(148, 163, 152);
    internal static readonly Color Green = Color.FromArgb(67, 255, 120);
    internal static readonly Color Accent = Green;
    internal static readonly Color Border = Color.FromArgb(29, 43, 32);
    internal static readonly Font Font = new("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

    internal static Label Label(string text, float size, Color color, bool bold = false) =>
        new()
        {
            Text = text,
            AutoSize = false,
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point)
        };

    internal static Button Button(string text, bool primary = false) =>
        new()
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Green : Panel,
            ForeColor = primary ? Background : Text,
            FlatAppearance = { BorderColor = primary ? Green : Border, BorderSize = 1 },
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            Cursor = Cursors.Hand
        };
}
