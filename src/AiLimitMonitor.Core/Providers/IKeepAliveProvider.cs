namespace AiLimitMonitor.Core.Providers;

/// <summary>Outcome of one keep-alive call: what was sent and what came back (single-line summaries).</summary>
public sealed record KeepAliveResult(bool Success, string RequestContent, string ResponseContent);

/// <summary>
/// A provider that can start a new 5h rate-limit window by sending a minimal message
/// ("hello") to the platform, so the window starts counting immediately after a reset.
/// </summary>
public interface IKeepAliveProvider
{
    string Name { get; }
    Task<KeepAliveResult> SendHelloAsync(CancellationToken cancellationToken);
}
