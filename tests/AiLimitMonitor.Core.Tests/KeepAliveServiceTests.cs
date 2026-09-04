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
    public void Sends_hello_when_codex_reports_an_untouched_window_a_full_length_ahead()
    {
        // Codex never omits reset_at: an untouched 5h window simply resets 5h from now, so
        // reading it the claude way ("reset in the future = running") silenced codex forever.
        // Observed live 2026/09/04: used_percent 0, reset_after_seconds 18000.
        var window = new UsageWindow("5h", 0, Now.AddHours(5), TimeSpan.FromHours(5));

        Assert.Equal(KeepAliveDecision.SendHello, Evaluate(Usage(window), out _));
    }

    [Fact]
    public void DoesNothing_when_codex_window_has_started_counting_but_still_reads_zero_percent()
    {
        // The hello is too small to move used_percent off 0 (observed live 2026/09/04), so the
        // only trace that the window is running is its reset drifting in from the full length.
        // The first minutes of that drift are inside IdleWindowTolerance and are covered by
        // Cooldown instead; from then on the elapsed time itself is the signal.
        var window = new UsageWindow("5h", 0, Now.AddHours(5) - KeepAliveService.Cooldown, TimeSpan.FromHours(5));

        Assert.Equal(KeepAliveDecision.None, Evaluate(Usage(window), out _));
    }

    [Fact]
    public void Sends_hello_when_codex_reset_lags_the_full_length_only_by_round_trip_delay()
    {
        // The platform stamps reset_at before we compare it to our own clock, so an untouched
        // window lands a shade under a full length ahead. That gap must not read as usage.
        var window = new UsageWindow("5h", 0, Now.AddHours(5).AddSeconds(-2), TimeSpan.FromHours(5));

        Assert.Equal(KeepAliveDecision.SendHello, Evaluate(Usage(window), out _));
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
    public void FormatLogLine_appends_tokens_and_running_total()
    {
        var line = KeepAliveService.FormatLogLine(
            new DateTimeOffset(2026, 8, 8, 15, 4, 5, TimeSpan.FromHours(8)),
            "claude",
            new KeepAliveResult(true, "hello", "HTTP 200: Hi!", new TokenUsage(12, 5)),
            new TokenUsage(120, 50));

        Assert.Equal(
            "[2026/08/08 15:04:05 +08:00] claude: hello → HTTP 200: Hi! " +
            "[tokens in=12 out=5 total=17 | 累計 in=120 out=50 total=170]",
            line);
    }

    [Fact]
    public void FormatLogLine_omits_tokens_when_the_call_reported_none()
    {
        // Skips and failures never reach the model — a "0 tokens" note would only mislead.
        var line = KeepAliveService.FormatLogLine(
            new DateTimeOffset(2026, 8, 8, 15, 4, 5, TimeSpan.FromHours(8)),
            "claude",
            new KeepAliveResult(true, "(skip)", "使用中"));

        Assert.DoesNotContain("tokens", line);
    }

    [Fact]
    public void ParseLoggedTokens_reads_back_the_per_call_counts()
    {
        var line = KeepAliveService.FormatLogLine(
            new DateTimeOffset(2026, 8, 8, 15, 4, 5, TimeSpan.FromHours(8)),
            "claude-5x",
            new KeepAliveResult(true, "hello (model=x)", "HTTP 200: Hi!", new TokenUsage(12, 5)),
            new TokenUsage(120, 50));

        var parsed = KeepAliveService.ParseLoggedTokens(line);

        Assert.NotNull(parsed);
        Assert.Equal("claude-5x", parsed.Value.Provider);
        // The per-call counts, not the cumulative ones that follow them on the same line.
        Assert.Equal(new TokenUsage(12, 5), parsed.Value.Tokens);
    }

    [Fact]
    public void ParseLoggedTokens_ignores_lines_without_tokens()
    {
        Assert.Null(KeepAliveService.ParseLoggedTokens(
            "[2026/08/08 15:04:05 +08:00] claude: (skip) → 使用中（5h 已用 12%）"));
        Assert.Null(KeepAliveService.ParseLoggedTokens("garbage"));
    }

    [Fact]
    public void TotalTokensFor_sums_previous_runs_from_the_log()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keepalive-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, [
            "[2026/08/08 15:04:05 +08:00] claude: hello → HTTP 200: Hi! [tokens in=12 out=5 total=17]",
            "[2026/08/08 20:04:05 +08:00] claude: hello → HTTP 200: Hi! [tokens in=10 out=3 total=13 | 累計 in=22 out=8 total=30]",
            "[2026/08/08 21:04:05 +08:00] codex: (skip) → 使用中",
        ], System.Text.Encoding.UTF8);
        try
        {
            var service = new KeepAliveService([], path);

            Assert.Equal(new TokenUsage(22, 8), service.TotalTokensFor("claude"));
            Assert.Null(service.TotalTokensFor("codex"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TotalTokensFor_reloads_when_log_file_is_updated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"keepalive-{Guid.NewGuid():N}.log");
        File.WriteAllLines(path, [
            "[2026/08/08 15:04:05 +08:00] claude: hello → HTTP 200: Hi! [tokens in=12 out=5 total=17]",
        ], System.Text.Encoding.UTF8);
        try
        {
            var service = new KeepAliveService([], path);
            Assert.Equal(new TokenUsage(12, 5), service.TotalTokensFor("claude"));

            // File updated later
            File.AppendAllLines(path, [
                "[2026/08/08 20:04:05 +08:00] codex: hello → HTTP 200: Hi! [tokens in=15 out=4 total=19]",
            ], System.Text.Encoding.UTF8);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            Assert.Equal(new TokenUsage(15, 4), service.TotalTokensFor("codex"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExtractSseTokenUsage_reads_root_level_usage()
    {
        const string sse = "data: {\"type\":\"response.done\",\"usage\":{\"input_tokens\":40,\"output_tokens\":10}}\n\n" +
                           "data: [DONE]\n";

        Assert.Equal(new TokenUsage(40, 10), CodexUsageProvider.ExtractSseTokenUsage(sse));
    }

    [Fact]
    public void ExtractTokenUsage_folds_cache_tokens_into_the_input_side()
    {
        const string json = """
            { "usage": { "input_tokens": 20, "cache_creation_input_tokens": 4,
                         "cache_read_input_tokens": 6, "output_tokens": 9 } }
            """;

        Assert.Equal(new TokenUsage(30, 9), ClaudeUsageProvider.ExtractTokenUsage(json));
    }

    [Fact]
    public void ExtractTokenUsage_returns_null_without_a_usage_block()
    {
        Assert.Null(ClaudeUsageProvider.ExtractTokenUsage("""{ "content": [] }"""));
        Assert.Null(ClaudeUsageProvider.ExtractTokenUsage("not json"));
    }

    [Fact]
    public void ExtractSseTokenUsage_reads_the_completed_event()
    {
        const string sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\n" +
                           "data: {\"type\":\"response.completed\",\"response\":{\"usage\":" +
                           "{\"input_tokens\":31,\"output_tokens\":7,\"total_tokens\":38}}}\n\n" +
                           "data: [DONE]\n";

        Assert.Equal(new TokenUsage(31, 7), CodexUsageProvider.ExtractSseTokenUsage(sse));
    }

    [Fact]
    public void ExtractSseTokenUsage_returns_null_when_the_stream_carried_no_usage()
    {
        Assert.Null(CodexUsageProvider.ExtractSseTokenUsage(
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\ndata: [DONE]\n"));
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
