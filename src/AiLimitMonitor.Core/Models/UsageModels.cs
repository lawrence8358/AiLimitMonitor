namespace AiLimitMonitor.Core.Models;

/// <summary>One rate-limit window, e.g. "5h" or "weekly".</summary>
public sealed record UsageWindow(string Label, double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>Usage result for a single provider (one account).</summary>
public sealed record ProviderUsage(
    string Name,
    IReadOnlyList<UsageWindow> Windows,
    IReadOnlyList<string> Notes,
    string? Error = null)
{
    public static ProviderUsage Failed(string name, string error) => new(name, [], [], error);
}

/// <summary>All providers fetched at one point in time.</summary>
public sealed record MonitorSnapshot(DateTimeOffset Timestamp, IReadOnlyList<ProviderUsage> Providers)
{
    /// <summary>The earliest upcoming reset across all providers, or null if none known.</summary>
    public DateTimeOffset? NextReset(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        foreach (var p in Providers)
            foreach (var w in p.Windows)
                if (w.ResetsAt is { } r && r > now && (next is null || r < next))
                    next = r;
        return next;
    }
}
