using Azure.Data.Tables;
using Microsoft.Extensions.Options;

namespace YieldDataLogger.Api.Storage.Tables;

/// <summary>
/// Ensures the PriceTicks table exists before the first write. Registered as an
/// IHostedService so it runs once at startup, ahead of the collector sources.
/// </summary>
public sealed class TablesInitializer : IHostedService
{
    private readonly TableServiceClient _service;
    private readonly IOptions<StorageOptions> _options;
    private readonly ILogger<TablesInitializer> _logger;

    public TablesInitializer(TableServiceClient service, IOptions<StorageOptions> options, ILogger<TablesInitializer> logger)
    {
        _service = service;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var name = _options.Value.Tables.PriceTicksTableName;
        await _service.CreateTableIfNotExistsAsync(name, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Table Storage ready: {Table}", name);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
