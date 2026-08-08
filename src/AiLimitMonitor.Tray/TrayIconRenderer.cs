namespace AiLimitMonitor.Tray;

/// <summary>Draws short text like "3h" or "58m" onto a 32x32 tray icon.</summary>
internal static class TrayIconRenderer
{
    public static Icon Render(string text)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.FromArgb(30, 30, 30));

        var fontSize = text.Length switch
        {
            <= 2 => 15f,
            3 => 12f,
            _ => 10f,
        };
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, Brushes.White,
            (32 - size.Width) / 2, (32 - size.Height) / 2);

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
