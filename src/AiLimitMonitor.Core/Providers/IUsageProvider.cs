using AiLimitMonitor.Core.Models;

namespace AiLimitMonitor.Core.Providers;

public interface IUsageProvider
{
    string Name { get; }
    Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken);
}
