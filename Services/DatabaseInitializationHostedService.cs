using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Monkasa.Services;

public sealed class DatabaseInitializationHostedService : IHostedService
{
    private readonly SqliteThumbnailCacheStore _cacheStore;
    private readonly ILogger<DatabaseInitializationHostedService> _logger;

    public DatabaseInitializationHostedService(
        SqliteThumbnailCacheStore cacheStore,
        ILogger<DatabaseInitializationHostedService> logger)
    {
        _cacheStore = cacheStore;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _cacheStore.EnsureSchemaAsync(cancellationToken);
        _logger.LogInformation("SQLite schema check completed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
