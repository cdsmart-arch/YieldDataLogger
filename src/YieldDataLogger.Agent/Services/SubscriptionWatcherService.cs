using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YieldDataLogger.Agent.Services;

/// <summary>
/// Watches the subscriptions.json file for edits (typically from the Manager) and feeds
/// each reload through the <see cref="SubscriptionManager"/>, which in turn broadcasts the
/// diff so the hub client can Subscribe / Unsubscribe live without a restart.
///
/// FileSystemWatcher events are notoriously bursty (editors write the file multiple times,
/// antivirus products can double-fire); we debounce with a short delay and swallow errors.
/// </summary>
public sealed class SubscriptionWatcherService : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly SubscriptionStore _store;
    private readonly SubscriptionManager _manager;
    private readonly ILogger<SubscriptionWatcherService> _logger;

    public SubscriptionWatcherService(
        SubscriptionStore store,
        SubscriptionManager manager,
        ILogger<SubscriptionWatcherService> logger)
    {
        _store = store;
        _manager = manager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dir  = Path.GetDirectoryName(_store.FilePath)!;
        var file = Path.GetFileName(_store.FilePath);
        Directory.CreateDirectory(dir);

        using var watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        DateTime pendingUntil = DateTime.MinValue;
        var trigger = new SemaphoreSlim(0, 1);

        void Schedule()
        {
            pendingUntil = DateTime.UtcNow + Debounce;
            try { trigger.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }

        watcher.Changed += (_, _) => Schedule();
        watcher.Created += (_, _) => Schedule();
        watcher.Renamed += (_, _) => Schedule();

        _logger.LogInformation("Watching {Path} for subscription changes.", _store.FilePath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await trigger.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }

            // Debounce: wait until the burst settles, then reload once.
            while (!stoppingToken.IsCancellationRequested)
            {
                var remaining = pendingUntil - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                try { await Task.Delay(remaining, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }

            try
            {
                var loaded = _store.Load();
                if (loaded is null)
                {
                    _logger.LogWarning("Subscriptions file disappeared or failed to parse; ignoring event.");
                    continue;
                }
                _manager.UpdateTo(loaded.Symbols);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply subscription update.");
            }
        }
    }
}
