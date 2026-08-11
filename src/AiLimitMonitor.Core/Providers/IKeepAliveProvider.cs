namespace AiLimitMonitor.Core.Providers;

/// <summary>
/// Tokens billed for one keep-alive call, as reported by the platform. Cached/cache-write
/// tokens are folded into <see cref="InputTokens"/> — they are still billed input, and a
/// hello never actually hits the cache, so splitting them out would only add noise.
/// </summary>
public sealed record TokenUsage(long InputTokens, long OutputTokens)
{
    public long TotalTokens => InputTokens + OutputTokens;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens);

    /// <summary>"in=12 out=5 total=17" — the shape written to (and parsed back from) the log.</summary>
    public override string ToString() => $"in={InputTokens} out={OutputTokens} total={TotalTokens}";
}

/// <summary>Outcome of one keep-alive call: what was sent, what came back (single-line
/// summaries), and what it cost in tokens (null when the platform reported none).</summary>
public sealed record KeepAliveResult(
    bool Success, string RequestContent, string ResponseContent, TokenUsage? Tokens = null);

/// <summary>
/// A provider that can start a new 5h rate-limit window by sending a minimal message
/// ("hello") to the platform, so the window starts counting immediately after a reset.
/// </summary>
public interface IKeepAliveProvider
{
    string Name { get; }
    Task<KeepAliveResult> SendHelloAsync(CancellationToken cancellationToken);
}
