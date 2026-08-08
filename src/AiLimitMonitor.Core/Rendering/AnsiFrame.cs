namespace AiLimitMonitor.Core.Rendering;

/// <summary>ANSI sequences for flicker-free full-screen redraws in a terminal.</summary>
public static class AnsiFrame
{
    /// <summary>Switch to the alternate screen buffer (own blank screen, like htop), clear it, hide the cursor.</summary>
    public const string EnterAltScreen = "\x1b[?1049h\x1b[2J\x1b[H\x1b[?25l";

    /// <summary>Restore the cursor and the original screen content.</summary>
    public const string LeaveAltScreen = "\x1b[?25h\x1b[?1049l";

    /// <summary>
    /// Homes the cursor and rewrites the whole frame in place, erasing the leftover tail of every
    /// rewritten line and the remainder of the screen. Line endings are normalized to "\n" first:
    /// the erase sequence must sit between the line text and the line break — after a carriage
    /// return it would move to column 0 and wipe the line that was just written.
    /// </summary>
    public static string Compose(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        return "\x1b[H" + normalized.Replace("\n", "\x1b[K\n") + "\x1b[0J";
    }
}
