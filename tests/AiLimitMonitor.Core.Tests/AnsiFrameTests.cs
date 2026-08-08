using AiLimitMonitor.Core.Rendering;

namespace AiLimitMonitor.Core.Tests;

public class AnsiFrameTests
{
    [Fact]
    public void Compose_puts_erase_between_text_and_line_break()
    {
        var frame = AnsiFrame.Compose("claude\nline2\n");

        Assert.Equal("\x1b[Hclaude\x1b[K\nline2\x1b[K\n\x1b[0J", frame);
    }

    [Fact]
    public void Compose_normalizes_crlf_so_erase_never_follows_carriage_return()
    {
        // A "\r" before "\x1b[K" would move the cursor to column 0 and erase the line
        // that was just written, blanking the whole screen.
        var frame = AnsiFrame.Compose("claude\r\nline2\r\n");

        Assert.DoesNotContain('\r', frame);
        Assert.Equal("\x1b[Hclaude\x1b[K\nline2\x1b[K\n\x1b[0J", frame);
    }

    [Fact]
    public void Compose_clears_rest_of_screen_at_end() =>
        Assert.EndsWith("\x1b[0J", AnsiFrame.Compose("x"));
}
