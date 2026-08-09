using AiLimitMonitor.Core;
using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core.Tests;

public class KeepAliveServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.FromHours(8));

    private static ProviderUsage Usage(params UsageWindow[] windows) => new("claude", windows, []);

    private static KeepAliveDecision Evaluate(ProviderUsage usage, out double usedPercent) =>
        KeepAliveService.Evaluate(usage, Now, out usedPercent);

    [Fact]
    public void SendsHello_when_5h_window_elapsed_at_zero_usage()
    {
        var usage = Usage(new UsageWindow("5h", 0, Now.AddMinutes(-1)),
            new UsageWindow("weekly", 40, Now.AddDays(2)));

        Assert.Equal(KeepAliveDecision.SendHello, Evaluate(usage, out _));
    }

    [Fact]
    public void SendsHello_when_5h_window_is_idle_without_reset_time()
    {
        Assert.Equal(KeepAliveDecision.SendHello,
            Evaluate(Usage(new UsageWindow("5h", 0, null)), out _));
    }

    [Fact]
    public void SkipsInUse_when_elapsed_5h_window_shows_usage()
    {
        // Usage > 0 with no running window means someone is (or was just) using the account:
        // the log records the percentage so "not called" is distinguishable from "nothing happened".
        var usage = Usage(new UsageWindow("5h", 12.5, Now.AddMinutes(-1)));

        Assert.Equal(KeepAliveDecision.SkipInUse, Evaluate(usage, out var percent));
        Assert.Equal(12.5, percent);
    }

    [Fact]
    public void DoesNothing_when_plan_has_no_5h_window()
    {
        // e.g. codex team plan reports only a weekly window — there is no 5h clock to restart.
        Assert.Equal(KeepAliveDecision.None,
            Evaluate(Usage(new UsageWindow("weekly", 40, Now.AddDays(2))), out _));
    }

    [Fact]
    public void DoesNothing_when_5h_window_is_active()
    {
        Assert.Equal(KeepAliveDecision.None,
            Evaluate(Usage(new UsageWindow("5h", 12, Now.AddHours(4))), out _));
    }

    [Fact]
    public void DoesNothing_when_window_started_by_hello_still_reads_zero_percent()
    {
        // Right after an auto-hello the usage API reports 0.0% (the hello is too small to
        // register) but resets_at is ~5h ahead. The running-window check must win over the
        // 0% rule, or the monitor would hello itself in a loop. Observed live 2026/08/09.
        Assert.Equal(KeepAliveDecision.None,
            Evaluate(Usage(new UsageWindow("5h", 0, Now.AddHours(4).AddMinutes(58))), out _));
    }

    [Fact]
    public void DoesNothing_when_any_window_is_exhausted()
    {
        // e.g. weekly at 100%: the hello would be rejected anyway.
        var usage = Usage(new UsageWindow("5h", 0, null), new UsageWindow("weekly", 100, Now.AddDays(1)));

        Assert.Equal(KeepAliveDecision.None, Evaluate(usage, out _));
    }

    [Fact]
    public void SendsHello_when_exhausted_windows_have_already_reset()
    {
        var usage = Usage(new UsageWindow("5h", 0, Now.AddMinutes(-5)),
            new UsageWindow("weekly", 100, Now.AddMinutes(-5)));

        Assert.Equal(KeepAliveDecision.SendHello, Evaluate(usage, out _));
    }

    [Fact]
    public void DoesNothing_when_fetch_failed()
    {
        Assert.Equal(KeepAliveDecision.None,
            Evaluate(ProviderUsage.Failed("claude", "HTTP 500"), out _));
    }

    [Fact]
    public void KeepAliveResolved_defaults_to_claude_only()
    {
        Assert.True(new Config.ProviderConfig { Type = "claude" }.KeepAliveResolved);
        Assert.False(new Config.ProviderConfig { Type = "codex" }.KeepAliveResolved);
        Assert.True(new Config.ProviderConfig { Type = "codex", KeepAlive = true }.KeepAliveResolved);
        Assert.False(new Config.ProviderConfig { Type = "claude", KeepAlive = false }.KeepAliveResolved);
    }

    [Fact]
    public void Sanitize_collapses_newlines_and_truncates()
    {
        Assert.Equal("a b c", KeepAliveService.Sanitize("a\r\n b\t\tc", 100));
        Assert.Equal("abcde", KeepAliveService.Sanitize("abcdefgh", 5));
        Assert.Equal("(empty)", KeepAliveService.Sanitize("   ", 100));
    }

    [Fact]
    public void FormatLogLine_is_single_bracketed_line()
    {
        var line = KeepAliveService.FormatLogLine(
            new DateTimeOffset(2026, 8, 8, 15, 4, 5, TimeSpan.FromHours(8)),
            "claude",
            new KeepAliveResult(true, "hello", "HTTP 200: Hi!\nHow can I help?"));

        Assert.Equal("[2026/08/08 15:04:05 +08:00] claude: hello → HTTP 200: Hi! How can I help?", line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void ExtractAssistantText_reads_first_text_block()
    {
        const string json = """
            { "content": [ { "type": "text", "text": "Hello there!" } ] }
            """;
        Assert.Equal("Hello there!", ClaudeUsageProvider.ExtractAssistantText(json));
    }

    [Fact]
    public void ExtractAssistantText_falls_back_to_raw_body()
    {
        Assert.Equal("not json", ClaudeUsageProvider.ExtractAssistantText("not json"));
    }

    [Fact]
    public void ExtractSseText_reassembles_output_deltas()
    {
        const string sse = "event: response.output_text.delta\n" +
                           "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}\n\n" +
                           "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo!\"}\n\n" +
                           "data: [DONE]\n";
        Assert.Equal("Hello!", CodexUsageProvider.ExtractSseText(sse));
    }

    [Fact]
    public void ExtractSseText_falls_back_to_raw_body()
    {
        Assert.Equal("plain error", CodexUsageProvider.ExtractSseText("plain error"));
    }
}
