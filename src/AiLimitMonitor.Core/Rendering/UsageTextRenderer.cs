using System.Text;
using AiLimitMonitor.Core.Models;

namespace AiLimitMonitor.Core.Rendering;

/// <summary>
/// Renders a snapshot as plain monospace text, shared by the console app and the tray popup:
/// <code>
/// claude
///   5h     [██████░░░░░░]  51.0% used    resets in 3h14m     (Sun 00:10 UTC+8)
///   weekly [██████░░░░░░]  54.0% used    resets in 7h04m     (Sun 04:00 UTC+8)
/// </code>
/// </summary>
public sealed class UsageTextRenderer(TimeZoneInfo? timeZone = null)
{
    public const int BarWidth = 12;

    private readonly TimeZoneInfo _timeZone = timeZone ?? TimeZoneInfo.Local;

    public string Render(MonitorSnapshot snapshot)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var provider in snapshot.Providers)
        {
            if (!first)
                sb.AppendLine();
            first = false;

            sb.AppendLine(provider.Name);

            if (provider.Error is { } error)
            {
                sb.AppendLine($"  error: {error}");
                continue;
            }

            if (provider.Windows.Count == 0 && provider.Notes.Count == 0)
            {
                sb.AppendLine("  no usage data");
                continue;
            }

            var labelWidth = Math.Max(6, provider.Windows.Count == 0 ? 0 : provider.Windows.Max(w => w.Label.Length));
            foreach (var window in provider.Windows)
                sb.AppendLine(RenderWindow(window, labelWidth, snapshot.Timestamp));

            foreach (var note in provider.Notes)
                sb.AppendLine($"  {note}");
        }
        return sb.ToString();
    }

    private string RenderWindow(UsageWindow window, int labelWidth, DateTimeOffset now)
    {
        var line = $"  {window.Label.PadRight(labelWidth)} [{Bar(window.UsedPercent)}] {window.UsedPercent,5:0.0}% used";
        if (window.ResetsAt is { } resetsAt)
        {
            var remaining = TimeFormat.Duration(resetsAt - now);
            line += $"    resets in {remaining,-8} ({TimeFormat.ResetStamp(resetsAt, _timeZone)})";
        }
        return line;
    }

    public static string Bar(double percent, int width = BarWidth)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var filled = (int)Math.Round(clamped / 100 * width, MidpointRounding.AwayFromZero);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>
    /// Console header line with a live countdown, e.g.
    /// "AI Limit Monitor — updated 14:10:42, next refresh in 37s (Ctrl+C to exit)".
    /// </summary>
    public string Header(DateTimeOffset updatedAt, TimeSpan untilRefresh)
    {
        var seconds = Math.Max(0, (long)Math.Ceiling(untilRefresh.TotalSeconds));
        var local = TimeZoneInfo.ConvertTime(updatedAt, _timeZone);
        return $"AI Limit Monitor — updated {local:HH:mm:ss}, next refresh in {seconds}s (Ctrl+C to exit)";
    }

}
