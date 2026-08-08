using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core;

/// <summary>Fetches all configured providers in parallel and never throws per-provider.</summary>
public sealed class UsageMonitorService(IReadOnlyList<IUsageProvider> providers, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<MonitorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(providers.Select(p => SafeFetchAsync(p, cancellationToken)));
        return new MonitorSnapshot(_time.GetUtcNow(), results);
    }

    private static async Task<ProviderUsage> SafeFetchAsync(IUsageProvider provider, CancellationToken ct)
    {
        try
        {
            return await provider.FetchAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProviderUsage.Failed(provider.Name, ex.Message);
        }
    }
}
