using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.ExchangeFeed.Binance;
using YieldDataLogger.ExchangeFeed.Bybit;
using YieldDataLogger.ExchangeFeed.Feeds;

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

    private readonly List<(TradeFeed Feed, Task Task)> _running = new();
    private CancellationTokenSource? _feedsCts;
    private string? _appliedKey;
    private volatile IReadOnlyList<TradeFeed> _feeds = Array.Empty<TradeFeed>();

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
    public IReadOnlyList<TradeFeed> Feeds => _feeds;

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

        _feedsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _feedsCts.Token;
        var feeds = new List<TradeFeed>();
        var bybit   = new BybitClient(_http.CreateClient("exchange"));
        var binance = new BinanceClient(_http.CreateClient("exchange"));

        void Start(ExchangeConfig ex, string exchange, Func<string, IExchangeSource> source)
        {
            if (!ex.Enabled) { _logger.LogInformation("{Exchange} feeds disabled in config", exchange); return; }
            foreach (var symbol in ex.NormalizedSymbols())
            {
                var feed = new TradeFeed(source(symbol), folder, ex.HistoryDaysFor(symbol), ex.VolumeMultiplierFor(symbol),
                    DataPath, _loggers.CreateLogger($"{exchange}.{symbol}"));
                feeds.Add(feed);
                _running.Add((feed, Task.Run(() => feed.RunAsync(token))));
                _logger.LogInformation("{Exchange} {Symbol} -> {File} (historyDays={Days})",
                    exchange, symbol, feed.ScidPath, ex.HistoryDaysFor(symbol));
            }
        }

        Start(cfg.Bybit,   "Bybit",   s => new BybitSource(s, bybit, _loggers.CreateLogger($"Bybit.{s}")));
        Start(cfg.Binance, "Binance", s => new BinanceSource(s, binance, _loggers.CreateLogger($"Binance.{s}")));
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
        _feeds = Array.Empty<TradeFeed>();
    }
}
