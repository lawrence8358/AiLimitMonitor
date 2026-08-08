using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Rendering;

namespace AiLimitMonitor.Core.Tests;

public class UsageTextRendererTests
{
    private static readonly TimeZoneInfo UtcPlus8 =
        TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test+8", "test+8");

    // Saturday 2026-08-08 20:56 UTC+8
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 56, 0, TimeSpan.Zero);

    [Fact]
    public void Render_matches_expected_layout()
    {
        var snapshot = new MonitorSnapshot(Now,
        [
            new ProviderUsage("claude",
                [
                    new UsageWindow("5h", 51.0, Now.AddMinutes(3 * 60 + 14)),
                    new UsageWindow("weekly", 54.0, Now.AddMinutes(7 * 60 + 4)),
                ], []),
            new ProviderUsage("codex (plus)",
                [new UsageWindow("5h", 24.0, Now.AddMinutes(3 * 60 + 15))],
                ["reset credits: 1 available"]),
        ]);

        var text = new UsageTextRenderer(UtcPlus8).Render(snapshot);

        var expected = string.Join(Environment.NewLine,
            "claude",
            "  5h     [██████░░░░░░]  51.0% used    resets in 3h 14m   (2026/08/09 00:10 UTC+8)",
            "  weekly [██████░░░░░░]  54.0% used    resets in 7h 04m   (2026/08/09 04:00 UTC+8)",
            "",
            "codex (plus)",
            "  5h     [███░░░░░░░░░]  24.0% used    resets in 3h 15m   (2026/08/09 00:11 UTC+8)",
            "  reset credits: 1 available",
            "");
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Render_shows_provider_errors()
    {
        var snapshot = new MonitorSnapshot(Now,
            [ProviderUsage.Failed("claude-5x", "credentials not found")]);

        var text = new UsageTextRenderer(UtcPlus8).Render(snapshot);

        Assert.Contains("claude-5x", text);
        Assert.Contains("  error: credentials not found", text);
    }

    [Fact]
    public void Render_omits_reset_when_unknown()
    {
        var snapshot = new MonitorSnapshot(Now,
            [new ProviderUsage("x", [new UsageWindow("5h", 10, null)], [])]);

        var text = new UsageTextRenderer(UtcPlus8).Render(snapshot);

        Assert.DoesNotContain("resets in", text);
    }

    [Theory]
    [InlineData(0, "░░░░░░░░░░░░")]
    [InlineData(51, "██████░░░░░░")]
    [InlineData(100, "████████████")]
    [InlineData(150, "████████████")] // clamped
    public void Bar_fills_proportionally(double percent, string expected) =>
        Assert.Equal($"{expected}", UsageTextRenderer.Bar(percent));

    [Fact]
    public void Header_shows_update_time_in_time_zone_and_countdown()
    {
        var header = new UsageTextRenderer(UtcPlus8).Header(Now, TimeSpan.FromSeconds(37));

        Assert.Equal("AI Limit Monitor — updated 20:56:00, next refresh in 37s (Ctrl+C to exit)", header);
    }

    [Fact]
    public void Header_rounds_countdown_up_so_it_starts_at_the_full_interval()
    {
        var header = new UsageTextRenderer(UtcPlus8).Header(Now, TimeSpan.FromMilliseconds(59_900));

        Assert.Contains("next refresh in 60s", header);
    }

    [Fact]
    public void Header_never_shows_negative_countdown()
    {
        var header = new UsageTextRenderer(UtcPlus8).Header(Now, TimeSpan.FromSeconds(-3));

        Assert.Contains("next refresh in 0s", header);
    }

    [Fact]
    public void NextReset_returns_earliest_future_reset()
    {
        var snapshot = new MonitorSnapshot(Now,
        [
            new ProviderUsage("a", [new UsageWindow("5h", 1, Now.AddHours(5))], []),
            new ProviderUsage("b", [new UsageWindow("5h", 1, Now.AddHours(2)),
                                    new UsageWindow("w", 1, Now.AddHours(-1))], []),
        ]);

        Assert.Equal(Now.AddHours(2), snapshot.NextReset(Now));
    }
}
