using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core.Tests;

public class ClaudeParsingTests
{
    private const string SampleUsage = """
        {
          "five_hour": { "utilization": 51.0, "resets_at": "2026-08-08T00:10:00+08:00" },
          "seven_day": { "utilization": 54.0, "resets_at": "2026-08-08T04:00:00+08:00" },
          "seven_day_opus": null
        }
        """;

    [Fact]
    public void ParseUsage_maps_five_hour_and_seven_day_windows()
    {
        var usage = ClaudeUsageProvider.ParseUsage("claude", SampleUsage);

        Assert.Null(usage.Error);
        Assert.Equal(2, usage.Windows.Count);
        Assert.Equal(("5h", 51.0), (usage.Windows[0].Label, usage.Windows[0].UsedPercent));
        Assert.Equal(("weekly", 54.0), (usage.Windows[1].Label, usage.Windows[1].UsedPercent));
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 0, 10, 0, TimeSpan.FromHours(8)),
            usage.Windows[0].ResetsAt);
    }

    [Fact]
    public void ParseUsage_skips_missing_windows()
    {
        var usage = ClaudeUsageProvider.ParseUsage("claude", """{ "five_hour": { "utilization": 10 } }""");

        var window = Assert.Single(usage.Windows);
        Assert.Equal("5h", window.Label);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void ReadCredentials_reads_all_oauth_fields()
    {
        var credentials = ClaudeUsageProvider.ReadCredentials("""
            { "claudeAiOauth": { "accessToken": "sk-test-123", "refreshToken": "rt-1", "expiresAt": 1786123474525 } }
            """);

        Assert.Equal(new ClaudeUsageProvider.ClaudeCredentials("sk-test-123", "rt-1", 1786123474525), credentials);
    }

    [Fact]
    public void ReadCredentials_tolerates_missing_optional_fields()
    {
        var credentials = ClaudeUsageProvider.ReadCredentials(
            """{ "claudeAiOauth": { "accessToken": "sk-test-123" } }""");

        Assert.Equal("sk-test-123", credentials.AccessToken);
        Assert.Null(credentials.RefreshToken);
        Assert.Null(credentials.ExpiresAtUnixMs);
    }

    [Fact]
    public void ReadCredentials_throws_when_missing() =>
        Assert.Throws<KeyNotFoundException>(() => ClaudeUsageProvider.ReadCredentials("{}"));

    [Theory]
    [InlineData(0, true)]      // expires right now -> refresh
    [InlineData(30, true)]     // expires within the 1-minute buffer -> refresh
    [InlineData(300, false)]   // plenty of time left
    public void IsExpired_uses_one_minute_buffer(int secondsUntilExpiry, bool expected)
    {
        var now = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var credentials = new ClaudeUsageProvider.ClaudeCredentials(
            "tok", "rt", now.AddSeconds(secondsUntilExpiry).ToUnixTimeMilliseconds());

        Assert.Equal(expected, credentials.IsExpired(now));
    }

    [Fact]
    public void IsExpired_is_false_when_expiry_unknown() =>
        Assert.False(new ClaudeUsageProvider.ClaudeCredentials("tok", null, null).IsExpired(DateTimeOffset.UtcNow));

    [Fact]
    public void ParseTokenResponse_reads_rotated_tokens()
    {
        var tokens = ClaudeUsageProvider.ParseTokenResponse(
            """{ "access_token": "new-at", "refresh_token": "new-rt", "expires_in": 28800 }""");

        Assert.Equal(new ClaudeUsageProvider.TokenResponse("new-at", "new-rt", 28800), tokens);
    }

    [Fact]
    public void ParseTokenResponse_throws_without_access_token() =>
        Assert.Throws<KeyNotFoundException>(() => ClaudeUsageProvider.ParseTokenResponse("{}"));

    [Fact]
    public void UpdateCredentials_rewrites_tokens_and_preserves_other_fields()
    {
        var expiresAt = new DateTimeOffset(2026, 8, 8, 20, 0, 0, TimeSpan.Zero);

        var updated = ClaudeUsageProvider.UpdateCredentials("""
            {
              "claudeAiOauth": {
                "accessToken": "old-at", "refreshToken": "old-rt", "expiresAt": 1,
                "scopes": ["user:inference"], "subscriptionType": "max"
              },
              "organizationUuid": "org-1"
            }
            """,
            new ClaudeUsageProvider.TokenResponse("new-at", "new-rt", 28800), expiresAt);

        var credentials = ClaudeUsageProvider.ReadCredentials(updated);
        Assert.Equal("new-at", credentials.AccessToken);
        Assert.Equal("new-rt", credentials.RefreshToken);
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(), credentials.ExpiresAtUnixMs);
        Assert.Contains("\"subscriptionType\": \"max\"", updated);
        Assert.Contains("\"organizationUuid\": \"org-1\"", updated);
    }

    [Fact]
    public void UpdateCredentials_keeps_old_refresh_token_when_not_rotated()
    {
        var updated = ClaudeUsageProvider.UpdateCredentials(
            """{ "claudeAiOauth": { "accessToken": "old-at", "refreshToken": "old-rt" } }""",
            new ClaudeUsageProvider.TokenResponse("new-at", null, 28800),
            DateTimeOffset.UnixEpoch);

        Assert.Equal("old-rt", ClaudeUsageProvider.ReadCredentials(updated).RefreshToken);
    }
}

public class CodexParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private const string SampleUsage = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "primary_window": {
              "used_percent": 24, "limit_window_seconds": 18000, "reset_at": 1786356247
            },
            "secondary_window": {
              "used_percent": 37, "limit_window_seconds": 604800, "reset_after_seconds": 402000
            }
          },
          "rate_limit_reset_credits": { "available_count": 1 }
        }
        """;

    [Fact]
    public void ParseUsage_maps_windows_and_plan()
    {
        var usage = CodexUsageProvider.ParseUsage("codex", SampleUsage, Now);

        Assert.Equal("codex (plus)", usage.Name);
        Assert.Equal(2, usage.Windows.Count);
        Assert.Equal(("5h", 24.0), (usage.Windows[0].Label, usage.Windows[0].UsedPercent));
        Assert.Equal(("weekly", 37.0), (usage.Windows[1].Label, usage.Windows[1].UsedPercent));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1786356247), usage.Windows[0].ResetsAt);
        Assert.Equal(Now.AddSeconds(402000), usage.Windows[1].ResetsAt);
    }

    [Fact]
    public void ParseUsage_reports_reset_credits()
    {
        var usage = CodexUsageProvider.ParseUsage("codex", SampleUsage, Now);

        Assert.Equal("reset credits: 1 available", Assert.Single(usage.Notes));
    }

    [Fact]
    public void ParseUsage_handles_missing_secondary_window()
    {
        var usage = CodexUsageProvider.ParseUsage("codex", """
            {
              "plan_type": "team",
              "rate_limit": {
                "primary_window": { "used_percent": 90, "limit_window_seconds": 604800, "reset_at": 1786356247 },
                "secondary_window": null
              },
              "rate_limit_reset_credits": { "available_count": 0 }
            }
            """, Now);

        Assert.Equal("codex (team)", usage.Name);
        var window = Assert.Single(usage.Windows);
        Assert.Equal("weekly", window.Label);
        Assert.Empty(usage.Notes);
    }

    [Theory]
    [InlineData(18000, "5h")]
    [InlineData(604800, "weekly")]
    [InlineData(86400, "24h")]
    [InlineData(1800, "30m")]
    [InlineData(0, "limit")]
    public void WindowLabel_names_known_windows(long seconds, string expected) =>
        Assert.Equal(expected, CodexUsageProvider.WindowLabel(seconds));

    [Fact]
    public void ReadAuth_reads_token_and_account() =>
        Assert.Equal(("tok", "acc"), CodexUsageProvider.ReadAuth(
            """{ "tokens": { "access_token": "tok", "account_id": "acc" } }"""));

    [Fact]
    public void ReadAuth_throws_when_missing() =>
        Assert.Throws<KeyNotFoundException>(() => CodexUsageProvider.ReadAuth("""{ "tokens": {} }"""));
}

public class CommandParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseOutput_reads_windows_and_notes()
    {
        var usage = CommandUsageProvider.ParseOutput("claude-5x", """
            {
              "windows": [
                { "label": "5h", "usedPercent": 51.0, "resetsAt": "2026-08-08T14:00:00+00:00" },
                { "label": "weekly", "usedPercent": 54.0, "resetsInSeconds": 3600 }
              ],
              "notes": ["extra account"]
            }
            """, Now);

        Assert.Null(usage.Error);
        Assert.Equal(2, usage.Windows.Count);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), usage.Windows[0].ResetsAt);
        Assert.Equal(Now.AddSeconds(3600), usage.Windows[1].ResetsAt);
        Assert.Equal("extra account", Assert.Single(usage.Notes));
    }

    [Fact]
    public void ParseOutput_reports_invalid_json_as_error()
    {
        var usage = CommandUsageProvider.ParseOutput("custom", "not json", Now);

        Assert.NotNull(usage.Error);
        Assert.Contains("invalid JSON", usage.Error);
    }
}
