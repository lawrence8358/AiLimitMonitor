using System.Text.Json;
using AiLimitMonitor.Core.Models;

namespace AiLimitMonitor.Core.Providers;

/// <summary>
/// Reads the Codex CLI OAuth token from ~/.codex/auth.json and queries the
/// read-only ChatGPT usage endpoint.
/// </summary>
public sealed class CodexUsageProvider(string name, string authPath, HttpClient http, TimeProvider? time = null)
    : IUsageProvider
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => name;

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(authPath))
            return ProviderUsage.Failed(name, $"auth file not found: {authPath}");

        string token, accountId;
        try
        {
            (token, accountId) = ReadAuth(await File.ReadAllTextAsync(authPath, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return ProviderUsage.Failed(name, $"cannot read access token: {ex.Message}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return ProviderUsage.Failed(name,
                $"HTTP {(int)response.StatusCode} — run `codex` once to refresh the token");

        return ParseUsage(name, await response.Content.ReadAsStringAsync(cancellationToken), _time.GetUtcNow());
    }

    public static (string Token, string AccountId) ReadAuth(string authJson)
    {
        using var doc = JsonDocument.Parse(authJson);
        if (doc.RootElement.TryGetProperty("tokens", out var tokens) &&
            tokens.TryGetProperty("access_token", out var token) &&
            tokens.TryGetProperty("account_id", out var account) &&
            token.GetString() is { Length: > 0 } tokenValue &&
            account.GetString() is { Length: > 0 } accountValue)
            return (tokenValue, accountValue);
        throw new KeyNotFoundException("tokens.access_token / tokens.account_id missing");
    }

    public static ProviderUsage ParseUsage(string name, string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var displayName = root.TryGetProperty("plan_type", out var plan) &&
                          plan.GetString() is { Length: > 0 } planValue
            ? $"{name} ({planValue})"
            : name;

        var windows = new List<UsageWindow>();
        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            AddWindow(rateLimit, "primary_window", windows, now);
            AddWindow(rateLimit, "secondary_window", windows, now);
        }

        var notes = new List<string>();
        if (root.TryGetProperty("rate_limit_reset_credits", out var credits) &&
            credits.ValueKind == JsonValueKind.Object &&
            credits.TryGetProperty("available_count", out var count) &&
            count.ValueKind == JsonValueKind.Number &&
            count.GetInt32() > 0)
            notes.Add($"reset credits: {count.GetInt32()} available");

        return new ProviderUsage(displayName, windows, notes);
    }

    private static void AddWindow(JsonElement rateLimit, string property, List<UsageWindow> windows, DateTimeOffset now)
    {
        if (!rateLimit.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object)
            return;

        double percent = el.TryGetProperty("used_percent", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;

        long windowSeconds = el.TryGetProperty("limit_window_seconds", out var w) &&
                             w.ValueKind == JsonValueKind.Number
            ? w.GetInt64()
            : 0;

        DateTimeOffset? resetsAt = null;
        if (el.TryGetProperty("reset_at", out var r) && r.ValueKind == JsonValueKind.Number)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64());
        else if (el.TryGetProperty("reset_after_seconds", out var after) && after.ValueKind == JsonValueKind.Number)
            resetsAt = now.AddSeconds(after.GetDouble());

        windows.Add(new UsageWindow(WindowLabel(windowSeconds), percent, resetsAt));
    }

    public static string WindowLabel(long windowSeconds) => windowSeconds switch
    {
        18000 => "5h",
        604800 => "weekly",
        > 0 when windowSeconds % 3600 == 0 => $"{windowSeconds / 3600}h",
        > 0 => $"{windowSeconds / 60}m",
        _ => "limit",
    };
}
