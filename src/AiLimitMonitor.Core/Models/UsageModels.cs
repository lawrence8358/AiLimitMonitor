namespace AiLimitMonitor.Core.Models;

/// <summary>One rate-limit window, e.g. "5h" or "weekly".</summary>
/// <param name="Length">
/// The window's full duration, when the platform reports it. Codex always hands back a
/// <see cref="ResetsAt"/> in the future — an untouched window simply resets one full length
/// from now — so the reset time alone cannot say whether the window is actually counting.
/// Knowing the length makes that answerable: a window whose reset is a full length away has
/// not started. Null for platforms that omit the reset time entirely while idle (claude).
/// </param>
public sealed record UsageWindow(
    string Label, double UsedPercent, DateTimeOffset? ResetsAt, TimeSpan? Length = null);

/// <summary>One available Codex rate-limit reset credit and its optional expiration.</summary>
public sealed record ResetCredit(DateTimeOffset? ExpiresAt);

/// <summary>Summary and optional details for available Codex rate-limit reset credits.</summary>
public sealed record ResetCredits(int AvailableCount, IReadOnlyList<ResetCredit>? Credits = null);

/// <summary>Usage result for a single provider (one account).</summary>
public sealed record ProviderUsage(
    string Name,
    IReadOnlyList<UsageWindow> Windows,
    IReadOnlyList<string> Notes,
    string? Error = null,
    ResetCredits? ResetCredits = null)
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
