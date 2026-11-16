using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.ExchangeFeed.Bybit;

namespace YieldDataLogger.ExchangeFeed;

/// <summary>
/// Reads config.json every 15 s and (re)starts one feed per configured symbol whenever the
/// config - or whether the Sierra folder exists - changes. Each feed runs and retries on its own.
/// </summary>
public sealed class FeedSupervisor : BackgroundService
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);

    private readonly ServiceOptions _options;
    private readonly IHttpClientFactory _http;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<FeedSupervisor> _logger;

    private readonly List<(BybitSymbolFeed Feed, Task Task)> _running = new();
    private CancellationTokenSource? _feedsCts;
    private string? _appliedKey;
    private volatile IReadOnlyList<BybitSymbolFeed> _feeds = Array.Empty<BybitSymbolFeed>();

    public FeedSupervisor(IOptions<ServiceOptions> options, IHttpClientFactory http, ILoggerFactory loggers)
    {
        _options = options.Value;
        _http    = http;
        _loggers = loggers;
        _logger  = loggers.CreateLogger<FeedSupervisor>();
    }

    public string ConfigPath => Environment.ExpandEnvironmentVariables(_options.ConfigPath);
    public string DataPath   => Environment.ExpandEnvironmentVariables(_options.DataPath);
    public FeedConfig? Config { get; private set; }
    public string? Problem { get; private set; }
    public IReadOnlyList<BybitSymbolFeed> Feeds => _feeds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Exchange feed config: {Path}", ConfigPath);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ApplyConfigAsync(stoppingToken);
                try { await Task.Delay(PollEvery, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            await StopFeedsAsync();
        }
    }

    private async Task ApplyConfigAsync(CancellationToken ct)
    {
        FeedConfig cfg;
        try
        {
            cfg = FeedConfig.LoadOrCreate(ConfigPath);
        }
        catch (Exception ex)
        {
            // Keep running whatever was last applied; a half-saved edit shouldn't stop the feed.
            if (Problem is null || !Problem.StartsWith("config.json")) _logger.LogWarning(ex, "Could not read {Path}", ConfigPath);
            Problem = $"config.json could not be read: {ex.Message}";
            return;
        }

        var folder = Environment.ExpandEnvironmentVariables(cfg.SierraDataFolder ?? "");
        var folderOk = folder.Length > 0 && Directory.Exists(folder);
        var key = JsonSerializer.Serialize(cfg) + "|" + folderOk;
        Config = cfg;
        if (key == _appliedKey) return;
        _appliedKey = key;

        await StopFeedsAsync();

        if (!folderOk)
        {
            Problem = $"Sierra Data folder not found: '{folder}'. Set sierraDataFolder in {ConfigPath}.";
            _logger.LogWarning("{Problem}", Problem);
            return;
        }
        Problem = null;

        if (!cfg.Bybit.Enabled || cfg.Bybit.Symbols.Length == 0)
        {
            _logger.LogInformation("Bybit feed disabled in config");
            return;
        }

        _feedsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var client = new BybitClient(_http.CreateClient("bybit"));
        var feeds = new List<BybitSymbolFeed>();
        foreach (var symbol in cfg.Bybit.Symbols
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Select(s => s.Trim().ToUpperInvariant())
                     .Distinct())
        {
            var feed = new BybitSymbolFeed(symbol, folder, cfg.Bybit, client, DataPath,
                _loggers.CreateLogger($"Bybit.{symbol}"));
            feeds.Add(feed);
            _running.Add((feed, Task.Run(() => feed.RunAsync(_feedsCts.Token))));
            _logger.LogInformation("Bybit {Symbol} -> {File} (historyDays={Days})", symbol, feed.ScidPath, cfg.Bybit.HistoryDays);
        }
        _feeds = feeds;
    }

    private async Task StopFeedsAsync()
    {
        if (_feedsCts is null) return;
        _feedsCts.Cancel();
        try { await Task.WhenAll(_running.Select(r => r.Task)); } catch { /* cancelled */ }
        _running.Clear();
        _feedsCts.Dispose();
        _feedsCts = null;
        _feeds = Array.Empty<BybitSymbolFeed>();
    }
}
