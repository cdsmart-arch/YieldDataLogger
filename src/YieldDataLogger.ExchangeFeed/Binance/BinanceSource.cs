using Microsoft.Extensions.Logging;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Binance;

/// <summary>
/// Binance USDⓈ-M perpetuals. History: daily aggTrades archive. Gap fill: real aggTrades
/// with side from REST - first located by time (1 h windows), then paged forward by id up to
/// now - so gaps don't need repairing. Only a gap older than <see cref="MaxRestGap"/> is left
/// to the archive.
/// </summary>
public sealed class BinanceSource : IExchangeSource
{
    private const long HourMs = 3_600_000;
    private static readonly TimeSpan MaxRestGap = TimeSpan.FromDays(3);

    private readonly BinanceClient _client;
    private readonly ILogger _logger;

    public BinanceSource(string symbol, BinanceClient client, ILogger logger)
    {
        Symbol  = symbol;
        _client = client;
        _logger = logger;
    }

    public string Exchange => "Binance";
    public string Symbol { get; }

    public Task<SymbolInfo> GetSymbolInfoAsync(CancellationToken ct) => _client.GetSymbolInfoAsync(Symbol, ct);

    public string ArchiveFileName(DateOnly day) => $"binance-{Symbol}-aggTrades-{day:yyyy-MM-dd}.zip";

    public Task<bool> DownloadDayAsync(DateOnly day, string destination, CancellationToken ct) =>
        _client.DownloadDayAsync(Symbol, day, destination, ct);

    public IReadOnlyList<ExchangeTrade> ReadArchive(string path) => BinanceClient.ReadArchive(path);

    public ITradeStream CreateStream(ILogger logger) => new BinanceTradeStream(Symbol, logger);

    public async Task FillGapAsync(GapContext gap, CancellationToken ct)
    {
        var nowMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs  = gap.FromUnixUs == long.MinValue ? gap.TodayStartMs : gap.FromUnixUs / 1000;
        var startMs = Math.Max(fromMs, nowMs - (long)MaxRestGap.TotalMilliseconds);
        if (startMs > fromMs) gap.MarkApproximate(fromMs, startMs);   // too far back for REST: archive repairs it

        // Locate the first trade at/after startMs (inclusive: same-millisecond fills are
        // deduped by the feed), skipping quiet hours.
        long lastId = -1;
        for (var w = startMs; w <= nowMs && lastId < 0; w += HourMs)
        {
            var page = await _client.GetAggTradesInWindowAsync(Symbol, w, Math.Min(w + HourMs - 1, nowMs), ct);
            if (page.Count == 0) continue;
            gap.WriteTrades(page.Select(p => p.Trade).ToList());
            lastId = page[^1].Id;   // id paging below continues from here, across hours, up to now
        }
        if (lastId < 0) return;              // nothing traded since the last record

        // Page forward by id until caught up (a short page means we reached the newest trade).
        var pages = 0;
        while (true)
        {
            var page = await _client.GetAggTradesFromIdAsync(Symbol, lastId + 1, ct);
            if (page.Count == 0) break;
            gap.WriteTrades(page.Select(p => p.Trade).ToList());
            lastId = page[^1].Id;
            if (++pages % 20 == 0)
                gap.Progress($"Filling gap: at {DateTimeOffset.FromUnixTimeMilliseconds(page[^1].Trade.UnixUs / 1000).UtcDateTime:yyyy-MM-dd HH:mm} UTC");
            if (page.Count < 1000) break;
        }
        if (pages > 0)
            _logger.LogInformation("Binance {Symbol}: gap filled with real aggTrades ({Pages} pages)", Symbol, pages + 1);
    }
}
