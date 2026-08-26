using System.Net;
using AiLimitMonitor.Core;
using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core.Tests;

public class ProviderFetchTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("ailimit-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task Claude_fetch_sends_bearer_token_and_parses_response()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".credentials.json"),
            """{ "claudeAiOauth": { "accessToken": "sk-test" } }""");

        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            captured = request;
            return Respond(HttpStatusCode.OK,
                """{ "five_hour": { "utilization": 51.0, "resets_at": "2026-08-08T00:10:00+08:00" } }""");
        }));

        var usage = await new ClaudeUsageProvider("claude", _tempDir, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Equal(51.0, Assert.Single(usage.Windows).UsedPercent);
        Assert.Equal(ClaudeUsageProvider.UsageUrl, captured!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-test", captured.Headers.GetValues("Authorization").Single());
        Assert.Equal("oauth-2021-10-01", captured.Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public async Task Claude_fetch_reports_http_errors()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".credentials.json"),
            """{ "claudeAiOauth": { "accessToken": "sk-test" } }""");

        using var http = new HttpClient(new FakeHandler(_ => Respond(HttpStatusCode.Unauthorized, "{}")));

        var usage = await new ClaudeUsageProvider("claude", _tempDir, http)
            .FetchAsync(CancellationToken.None);

        Assert.NotNull(usage.Error);
        Assert.Contains("401", usage.Error);
    }

    [Fact]
    public async Task Claude_fetch_refreshes_expired_token_and_persists_it()
    {
        var credentialsPath = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(credentialsPath, """
            { "claudeAiOauth": { "accessToken": "old-at", "refreshToken": "old-rt", "expiresAt": 1, "subscriptionType": "max" } }
            """);

        string? refreshBody = null;
        string? usageAuth = null;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() == ClaudeUsageProvider.TokenUrl)
            {
                refreshBody = request.Content!.ReadAsStringAsync().Result;
                return Respond(HttpStatusCode.OK,
                    """{ "access_token": "new-at", "refresh_token": "new-rt", "expires_in": 28800 }""");
            }
            usageAuth = request.Headers.GetValues("Authorization").Single();
            return Respond(HttpStatusCode.OK, """{ "five_hour": { "utilization": 10.0 } }""");
        }));

        var usage = await new ClaudeUsageProvider("claude", _tempDir, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Equal("Bearer new-at", usageAuth);
        Assert.Contains("\"grant_type\":\"refresh_token\"", refreshBody);
        Assert.Contains("\"refresh_token\":\"old-rt\"", refreshBody);
        Assert.Contains(ClaudeUsageProvider.ClientId, refreshBody);

        var persisted = ClaudeUsageProvider.ReadCredentials(File.ReadAllText(credentialsPath));
        Assert.Equal("new-at", persisted.AccessToken);
        Assert.Equal("new-rt", persisted.RefreshToken);
        Assert.True(persisted.ExpiresAtUnixMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Claude_fetch_retries_once_after_rejected_token()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".credentials.json"), """
            { "claudeAiOauth": { "accessToken": "stale-at", "refreshToken": "rt", "expiresAt": 99999999999999 } }
            """);

        var usageCalls = 0;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() == ClaudeUsageProvider.TokenUrl)
                return Respond(HttpStatusCode.OK, """{ "access_token": "new-at", "expires_in": 28800 }""");
            usageCalls++;
            return request.Headers.GetValues("Authorization").Single() == "Bearer new-at"
                ? Respond(HttpStatusCode.OK, """{ "five_hour": { "utilization": 10.0 } }""")
                : Respond(HttpStatusCode.TooManyRequests, "{}");
        }));

        var usage = await new ClaudeUsageProvider("claude", _tempDir, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Equal(2, usageCalls);
    }

    [Fact]
    public async Task Claude_fetch_reports_failure_when_refresh_fails()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".credentials.json"), """
            { "claudeAiOauth": { "accessToken": "stale-at", "refreshToken": "rt", "expiresAt": 1 } }
            """);

        using var http = new HttpClient(new FakeHandler(request =>
            request.RequestUri!.ToString() == ClaudeUsageProvider.TokenUrl
                ? Respond(HttpStatusCode.BadRequest, "{}")
                : Respond(HttpStatusCode.Unauthorized, "{}")));

        var usage = await new ClaudeUsageProvider("claude", _tempDir, http)
            .FetchAsync(CancellationToken.None);

        Assert.NotNull(usage.Error);
        Assert.Contains("401", usage.Error);
        Assert.Contains("re-login", usage.Error);
    }

    [Fact]
    public async Task Claude_fetch_reports_missing_credentials_file()
    {
        using var http = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("no call expected")));

        var usage = await new ClaudeUsageProvider("claude", Path.Combine(_tempDir, "missing"), http)
            .FetchAsync(CancellationToken.None);

        Assert.NotNull(usage.Error);
        Assert.Contains("credentials not found", usage.Error);
    }

    [Fact]
    public async Task Codex_fetch_sends_account_header_and_parses_response()
    {
        var authPath = Path.Combine(_tempDir, "auth.json");
        File.WriteAllText(authPath,
            """{ "tokens": { "access_token": "tok", "account_id": "acc-1" } }""");

        HttpRequestMessage? captured = null;
        var requestCount = 0;
        using var http = new HttpClient(new FakeHandler(request =>
        {
            requestCount++;
            captured = request;
            return Respond(HttpStatusCode.OK, """
                {
                  "plan_type": "plus",
                  "rate_limit": {
                    "primary_window": { "used_percent": 24, "limit_window_seconds": 18000, "reset_at": 1786356247 }
                  }
                }
                """);
        }));

        var usage = await new CodexUsageProvider("codex", authPath, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Equal("codex (plus)", usage.Name);
        Assert.Equal("acc-1", captured!.Headers.GetValues("chatgpt-account-id").Single());
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task Codex_fetch_loads_reset_credit_expiration_with_same_auth_headers()
    {
        var authPath = Path.Combine(_tempDir, "auth.json");
        File.WriteAllText(authPath,
            """{ "tokens": { "access_token": "tok", "account_id": "acc-1" } }""");

        var requests = new List<(string Url, string Authorization, string AccountId)>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            requests.Add((request.RequestUri!.ToString(),
                request.Headers.GetValues("Authorization").Single(),
                request.Headers.GetValues("chatgpt-account-id").Single()));
            return request.RequestUri!.ToString() == CodexUsageProvider.UsageUrl
                ? Respond(HttpStatusCode.OK, """
                    {
                      "rate_limit": {
                        "primary_window": { "used_percent": 24, "limit_window_seconds": 18000 }
                      },
                      "rate_limit_reset_credits": { "available_count": 1 }
                    }
                    """)
                : Respond(HttpStatusCode.OK, """
                    {
                      "available_count": 1,
                      "credits": [
                        { "status": "available", "expires_at": "2026-09-17T00:00:00Z" }
                      ]
                    }
                    """);
        }));

        var usage = await new CodexUsageProvider("codex", authPath, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Equal(2, requests.Count);
        Assert.Equal(CodexUsageProvider.UsageUrl, requests[0].Url);
        Assert.Equal(CodexUsageProvider.ResetCreditsUrl, requests[1].Url);
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer tok", request.Authorization);
            Assert.Equal("acc-1", request.AccountId);
        });
        var credit = Assert.Single(usage.ResetCredits!.Credits!);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero), credit.ExpiresAt);
    }

    [Fact]
    public async Task Codex_fetch_keeps_usage_when_reset_credit_details_fail()
    {
        var authPath = Path.Combine(_tempDir, "auth.json");
        File.WriteAllText(authPath,
            """{ "tokens": { "access_token": "tok", "account_id": "acc-1" } }""");

        using var http = new HttpClient(new FakeHandler(request =>
            request.RequestUri!.ToString() == CodexUsageProvider.UsageUrl
                ? Respond(HttpStatusCode.OK, """
                    {
                      "rate_limit": {
                        "primary_window": { "used_percent": 24, "limit_window_seconds": 18000 }
                      },
                      "rate_limit_reset_credits": { "available_count": 1 }
                    }
                    """)
                : Respond(HttpStatusCode.TooManyRequests, "{}")));

        var usage = await new CodexUsageProvider("codex", authPath, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Single(usage.Windows);
        Assert.Equal(1, usage.ResetCredits!.AvailableCount);
        Assert.Null(usage.ResetCredits.Credits);
    }

    [Fact]
    public async Task Codex_fetch_ignores_invalid_reset_credit_numbers()
    {
        var authPath = Path.Combine(_tempDir, "auth.json");
        File.WriteAllText(authPath,
            """{ "tokens": { "access_token": "tok", "account_id": "acc-1" } }""");

        using var http = new HttpClient(new FakeHandler(request =>
            request.RequestUri!.ToString() == CodexUsageProvider.UsageUrl
                ? Respond(HttpStatusCode.OK,
                    """{ "rate_limit_reset_credits": { "available_count": 1 } }""")
                : Respond(HttpStatusCode.OK, """
                    {
                      "available_count": 999999999999999999999,
                      "credits": [
                        { "status": "available", "expires_at": 999999999999999999999 }
                      ]
                    }
                    """)));

        var usage = await new CodexUsageProvider("codex", authPath, http)
            .FetchAsync(CancellationToken.None);

        Assert.Null(usage.Error);
        Assert.Equal(1, usage.ResetCredits!.AvailableCount);
        Assert.Empty(usage.ResetCredits.Credits!);
    }

    [Fact]
    public async Task Monitor_service_isolates_provider_exceptions()
    {
        var service = new UsageMonitorService([new ThrowingProvider(), new StaticProvider()]);

        var snapshot = await service.FetchAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Providers.Count);
        Assert.Equal("boom", snapshot.Providers[0].Error);
        Assert.Null(snapshot.Providers[1].Error);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public string Name => "bad";
        public Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class StaticProvider : IUsageProvider
    {
        public string Name => "ok";
        public Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderUsage("ok", [], []));
    }
}
