namespace AiLimitMonitor.Core.Rendering;

public static class TimeFormat
{
    /// <summary>
    /// "2d 3h" (≥ 1 day), "4h 42m" (≥ 1 hour), "45m", "&lt;1m"; "now" when zero or negative.
    /// Units below the largest applicable scale are dropped: no days under a day,
    /// no hours under an hour. Units are separated by a space.
    /// </summary>
    public static string Duration(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "now";
        if (remaining.TotalDays >= 1)
        {
            var days = (int)remaining.TotalDays;
            return remaining.Hours > 0 ? $"{days}d {remaining.Hours}h" : $"{days}d";
        }
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m";
        if (remaining.Minutes >= 1)
            return $"{remaining.Minutes}m";
        return "<1m";
    }

    /// <summary>Single-unit form for the tray icon: "58m", "3h", "2d".</summary>
    public static string CompactDuration(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "0m";
        if (remaining.TotalMinutes < 60)
            return $"{Math.Max(1, (int)remaining.TotalMinutes)}m";
        if (remaining.TotalHours < 24)
            return $"{(int)remaining.TotalHours}h";
        return $"{(int)remaining.TotalDays}d";
    }

    /// <summary>"2026/08/08 18:59 UTC+8" in the given time zone (Taiwan-style date format).</summary>
    public static string ResetStamp(DateTimeOffset resetsAt, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTime(resetsAt, timeZone);
        return $"{local.ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} {OffsetLabel(local.Offset)}";
    }

    /// <summary>"UTC+8", "UTC-5", "UTC+5:30", "UTC".</summary>
    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
            return "UTC";
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"UTC{sign}{abs.Hours}"
            : $"UTC{sign}{abs.Hours}:{abs.Minutes:00}";
    }
}
