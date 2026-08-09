using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLimitMonitor.Core.Models;

namespace AiLimitMonitor.Core.Providers;

/// <summary>
/// Reads the Claude Code OAuth token from a config directory (e.g. ~/.claude or ~/.claude-5x)
/// and queries the read-only usage endpoint. Works for any number of accounts by pointing
/// each provider instance at a different config directory. When the access token is expired
/// (or the API rejects it) the refresh token is used to obtain a new one, which is persisted
/// back to .credentials.json exactly like Claude Code itself does.
/// </summary>
public sealed class ClaudeUsageProvider(
    string name, string configDir, HttpClient http, TimeProvider? time = null, string? keepAliveModel = null)
    : IUsageProvider, IKeepAliveProvider
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    public const string MessagesUrl = "https://api.anthropic.com/v1/messages";

    /// <summary>Cheapest model — a keep-alive hello should burn as little quota as possible.
    /// Overridable per provider via config (keepAliveModel).</summary>
    public const string DefaultKeepAliveModel = "claude-haiku-4-5-20251001";

    public const string HelloText = "Hello，請不要有任何回應";

    /// <summary>Claude Code's public OAuth client id.</summary>
    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => name;

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        var credentialsPath = Path.Combine(configDir, ".credentials.json");
        if (!File.Exists(credentialsPath))
            return ProviderUsage.Failed(name, $"credentials not found: {credentialsPath}");

        ClaudeCredentials credentials;
        try
        {
            credentials = ReadCredentials(await File.ReadAllTextAsync(credentialsPath, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return ProviderUsage.Failed(name, $"cannot read access token: {ex.Message}");
        }

        var now = _time.GetUtcNow();
        var token = credentials.AccessToken;
        var refreshed = false;

        // Proactively refresh an (almost) expired token so we never burn a request on it.
        if (credentials.IsExpired(now) && credentials.RefreshToken is not null)
        {
            var newToken = await TryRefreshAsync(credentialsPath, credentials.RefreshToken, cancellationToken);
            if (newToken is not null)
            {
                token = newToken;
                refreshed = true;
            }
        }

        var response = await GetUsageAsync(token, cancellationToken);
        try
        {
            // The API rejected a token we thought was still valid — refresh once and retry.
            if (!response.IsSuccessStatusCode && !refreshed && credentials.RefreshToken is not null)
            {
                var newToken = await TryRefreshAsync(credentialsPath, credentials.RefreshToken, cancellationToken);
                if (newToken is not null)
                {
                    response.Dispose();
                    response = await GetUsageAsync(newToken, cancellationToken);
                    refreshed = true;
                }
            }

            if (!response.IsSuccessStatusCode)
                return ProviderUsage.Failed(name,
                    $"HTTP {(int)response.StatusCode}" +
                    (refreshed ? " (after token refresh)" : " and token refresh failed — run `claude` to re-login"));

            return ParseUsage(name, await response.Content.ReadAsStringAsync(cancellationToken));
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Sends a minimal "hello" through the Messages API using the same OAuth token, which
    /// starts a fresh 5h window. OAuth tokens are only accepted when the request presents
    /// itself as Claude Code, hence the fixed system prompt.
    /// </summary>
    /// <summary>"hello (model=...)" — the log records which model each call used.</summary>
    private string HelloRequestLabel => $"{HelloText} (model={keepAliveModel ?? DefaultKeepAliveModel})";

    public async Task<KeepAliveResult> SendHelloAsync(CancellationToken cancellationToken)
    {
        var credentialsPath = Path.Combine(configDir, ".credentials.json");
        if (!File.Exists(credentialsPath))
            return new KeepAliveResult(false, HelloRequestLabel, $"credentials not found: {credentialsPath}");

        ClaudeCredentials credentials;
        try
        {
            credentials = ReadCredentials(await File.ReadAllTextAsync(credentialsPath, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return new KeepAliveResult(false, HelloRequestLabel, $"cannot read access token: {ex.Message}");
        }

        var token = credentials.AccessToken;
        var refreshed = false;
        if (credentials.IsExpired(_time.GetUtcNow()) && credentials.RefreshToken is not null)
        {
            var newToken = await TryRefreshAsync(credentialsPath, credentials.RefreshToken, cancellationToken);
            if (newToken is not null)
            {
                token = newToken;
                refreshed = true;
            }
        }

        var response = await PostHelloAsync(token, cancellationToken);
        try
        {
            if (!response.IsSuccessStatusCode && !refreshed && credentials.RefreshToken is not null)
            {
                var newToken = await TryRefreshAsync(credentialsPath, credentials.RefreshToken, cancellationToken);
                if (newToken is not null)
                {
                    response.Dispose();
                    response = await PostHelloAsync(newToken, cancellationToken);
                }
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var summary = response.IsSuccessStatusCode ? ExtractAssistantText(body) : body;
            return new KeepAliveResult(response.IsSuccessStatusCode, HelloRequestLabel,
                $"HTTP {(int)response.StatusCode}: {summary}");
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> PostHelloAsync(string token, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = keepAliveModel ?? DefaultKeepAliveModel,
            ["max_tokens"] = 16,
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "You are Claude Code, Anthropic's official CLI for Claude.",
            }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = HelloText,
            }),
        }.ToJsonString();

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // No anthropic-beta header here: /v1/messages rejects "oauth-2021-10-01"
        // (the usage endpoint still requires it) — verified 2026/08.
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>First text block of a Messages API response, or the raw body when the shape is unexpected.</summary>
    public static string ExtractAssistantText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("text", out var text) &&
                        text.GetString() is { Length: > 0 } value)
                        return value;
            }
        }
        catch (JsonException)
        {
        }
        return json;
    }

    private async Task<HttpResponseMessage> GetUsageAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2021-10-01");
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>Exchanges the refresh token and persists the rotated credentials. Returns null on failure.</summary>
    private async Task<string?> TryRefreshAsync(string credentialsPath, string refreshToken, CancellationToken cancellationToken)
    {
        // JsonObject instead of reflection-based serialization: safe under trimming.
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId,
        }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        TokenResponse tokens;
        try
        {
            tokens = ParseTokenResponse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return null;
        }

        // Persist before using: the refresh token may have been rotated and losing it would
        // force a re-login in Claude Code itself.
        var expiresAt = _time.GetUtcNow().AddSeconds(tokens.ExpiresInSeconds);
        var updated = UpdateCredentials(await File.ReadAllTextAsync(credentialsPath, cancellationToken), tokens, expiresAt);
        await File.WriteAllTextAsync(credentialsPath, updated, cancellationToken);

        return tokens.AccessToken;
    }

    public sealed record ClaudeCredentials(string AccessToken, string? RefreshToken, long? ExpiresAtUnixMs)
    {
        /// <summary>True when the token expires within the next minute.</summary>
        public bool IsExpired(DateTimeOffset now) =>
            ExpiresAtUnixMs is { } ms && ms <= now.AddMinutes(1).ToUnixTimeMilliseconds();
    }

    public sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

    public static ClaudeCredentials ReadCredentials(string credentialsJson)
    {
        using var doc = JsonDocument.Parse(credentialsJson);
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
            !oauth.TryGetProperty("accessToken", out var token) ||
            token.GetString() is not { Length: > 0 } accessToken)
            throw new KeyNotFoundException("claudeAiOauth.accessToken missing");

        string? refreshToken = oauth.TryGetProperty("refreshToken", out var refresh) &&
                               refresh.ValueKind == JsonValueKind.String
            ? refresh.GetString()
            : null;
        long? expiresAt = oauth.TryGetProperty("expiresAt", out var expires) &&
                          expires.ValueKind == JsonValueKind.Number
            ? expires.GetInt64()
            : null;

        return new ClaudeCredentials(accessToken, refreshToken, expiresAt);
    }

    public static TokenResponse ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) ||
            token.GetString() is not { Length: > 0 } accessToken)
            throw new KeyNotFoundException("access_token missing");

        string? refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var refresh) &&
                               refresh.ValueKind == JsonValueKind.String
            ? refresh.GetString()
            : null;
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expires) &&
                        expires.ValueKind == JsonValueKind.Number
            ? expires.GetInt32()
            : 3600;

        return new TokenResponse(accessToken, refreshToken, expiresIn);
    }

    /// <summary>Rewrites claudeAiOauth.* in the credentials file, preserving every other field.</summary>
    public static string UpdateCredentials(string credentialsJson, TokenResponse tokens, DateTimeOffset expiresAt)
    {
        var root = JsonNode.Parse(credentialsJson) ?? new JsonObject();
        if (root["claudeAiOauth"] is not JsonObject oauth)
        {
            oauth = new JsonObject();
            root["claudeAiOauth"] = oauth;
        }
        oauth["accessToken"] = tokens.AccessToken;
        if (tokens.RefreshToken is not null)
            oauth["refreshToken"] = tokens.RefreshToken;
        oauth["expiresAt"] = expiresAt.ToUnixTimeMilliseconds();
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static ProviderUsage ParseUsage(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var windows = new List<UsageWindow>();
        AddWindow(doc.RootElement, "five_hour", "5h", windows);
        AddWindow(doc.RootElement, "seven_day", "weekly", windows);
        return new ProviderUsage(name, windows, []);
    }

    private static void AddWindow(JsonElement root, string property, string label, List<UsageWindow> windows)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object)
            return;

        double percent = el.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;
        DateTimeOffset? resetsAt = el.TryGetProperty("resets_at", out var r) &&
                                   r.ValueKind == JsonValueKind.String &&
                                   DateTimeOffset.TryParse(r.GetString(), out var parsed)
            ? parsed
            : null;
        windows.Add(new UsageWindow(label, percent, resetsAt));
    }
}
