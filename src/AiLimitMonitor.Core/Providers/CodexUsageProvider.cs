using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiLimitMonitor.Core.Models;

namespace AiLimitMonitor.Core.Providers;

/// <summary>
/// Reads the Codex CLI OAuth token from ~/.codex/auth.json and queries the
/// read-only ChatGPT usage endpoint.
/// </summary>
public sealed class CodexUsageProvider(
    string name, string authPath, HttpClient http, TimeProvider? time = null, string? keepAliveModel = null)
    : IUsageProvider, IKeepAliveProvider
{
    public const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    public const string ResponsesUrl = "https://chatgpt.com/backend-api/codex/responses";

    /// <summary>Cheapest (mini-tier) Codex model — ChatGPT accounts only accept the slugs the
    /// Codex CLI itself offers. Overridable per provider via config (keepAliveModel).</summary>
    public const string DefaultKeepAliveModel = "gpt-5.6-luna";

    public const string HelloText = "Hello，請不要有任何回應";

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

    /// <summary>
    /// Sends a minimal "hello" through the Codex responses endpoint (the same one the Codex
    /// CLI uses), which starts a fresh 5h window. The endpoint only answers in SSE, so the
    /// assistant text is re-assembled from the stream for the log.
    /// </summary>
    /// <summary>"hello (model=...)" — the log records which model each call used.</summary>
    private string HelloRequestLabel => $"{HelloText} (model={keepAliveModel ?? DefaultKeepAliveModel})";

    public async Task<KeepAliveResult> SendHelloAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(authPath))
            return new KeepAliveResult(false, HelloRequestLabel, $"auth file not found: {authPath}");

        string token, accountId;
        try
        {
            (token, accountId) = ReadAuth(await File.ReadAllTextAsync(authPath, cancellationToken));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
        {
            return new KeepAliveResult(false, HelloRequestLabel, $"cannot read access token: {ex.Message}");
        }

        var body = new JsonObject
        {
            ["model"] = keepAliveModel ?? DefaultKeepAliveModel,
            ["instructions"] = "You are Codex, a coding agent running in the Codex CLI.",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = HelloText,
                }),
            }),
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["tools"] = new JsonArray(),
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = false,
            ["store"] = false,
            ["stream"] = true,
            ["include"] = new JsonArray(),
        }.ToJsonString();

        using var request = new HttpRequestMessage(HttpMethod.Post, ResponsesUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        request.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("session_id", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var summary = response.IsSuccessStatusCode ? ExtractSseText(responseBody) : responseBody;
        return new KeepAliveResult(response.IsSuccessStatusCode, HelloRequestLabel,
            $"HTTP {(int)response.StatusCode}: {summary}");
    }

    /// <summary>Re-assembles the assistant text from SSE output deltas; falls back to the raw body.</summary>
    public static string ExtractSseText(string sseBody)
    {
        var text = new StringBuilder();
        foreach (var line in sseBody.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var payload = trimmed["data: ".Length..];
            if (payload == "[DONE]")
                break;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("type", out var type) &&
                    type.GetString() == "response.output_text.delta" &&
                    doc.RootElement.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.String)
                    text.Append(delta.GetString());
            }
            catch (JsonException)
            {
            }
        }
        return text.Length > 0 ? text.ToString() : sseBody;
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
