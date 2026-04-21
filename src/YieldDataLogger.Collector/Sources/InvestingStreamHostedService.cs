using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Collector.Configuration;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Collector.Pipeline;

namespace YieldDataLogger.Collector.Sources;

/// <summary>
/// Hosts the <see cref="IInvestingStreamClient"/>, wires tick events into the dispatcher,
/// and restarts the client on unrecoverable failures. Also watchdogs the "no ticks for a
/// while" case and can trip a fallback flag if we ever introduce one.
/// </summary>
public sealed class InvestingStreamHostedService : BackgroundService
{
    private static readonly TimeSpan SilentThreshold = TimeSpan.FromMinutes(5);

    private readonly IInvestingStreamClient _client;
    private readonly IOptionsMonitor<CollectorOptions> _options;
    private readonly InstrumentCatalog _catalog;
    private readonly TickDispatcher _dispatcher;
    private readonly ILogger<InvestingStreamHostedService> _logger;

    public InvestingStreamHostedService(
        IInvestingStreamClient client,
        IOptionsMonitor<CollectorOptions> options,
        InstrumentCatalog catalog,
        TickDispatcher dispatcher,
        ILogger<InvestingStreamHostedService> logger)
    {
        _client = client;
        _options = options;
        _catalog = catalog;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Investing.Enabled)
        {
            _logger.LogInformation("Investing stream disabled via config, exiting.");
            return;
        }

        _client.OnTick += async (tick, ct) =>
        {
            await _dispatcher.DispatchAsync(tick, ct).ConfigureAwait(false);
        };

        var pids = _catalog.ByPid.Keys.ToList();
        if (pids.Count == 0)
        {
            _logger.LogWarning("No Investing pids in catalog; stream client will not start.");
            return;
        }

        try
        {
            await _client.StartAsync(pids, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Investing stream client failed to start");
            return;
        }

        // Watchdog loop - just reports health; reconnect handling is inside the client itself.
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var last = _client.LastTickUtc;
            var age = last == DateTime.MinValue
                ? TimeSpan.MaxValue
                : DateTime.UtcNow - last;

            if (age > SilentThreshold)
            {
                _logger.LogWarning(
                    "Investing stream silent for {Age}. IsLive={IsLive}. May need fallback to Playwright.",
                    age, _client.IsLive);
            }
        }

        try { await _client.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
    }
}
