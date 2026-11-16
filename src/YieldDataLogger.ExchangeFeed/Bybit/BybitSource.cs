using Microsoft.Extensions.Logging;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Bybit;

/// <summary>
/// Bybit linear perpetuals. History: daily trade archive (public.bybit.com). Gap fill: Bybit's
/// REST only returns the last 1000 trades, so a longer gap is bridged with minute klines
/// (sideless, flagged approximate) and repaired from the archive once it's published.
/// </summary>
public sealed class BybitSource : IExchangeSource
{
    private const long MinuteMs = 60_000;
    private static readonly TimeSpan MaxKlineGap = TimeSpan.FromDays(3);

    private readonly BybitClient _client;
    private readonly ILogger _logger;

    public BybitSource(string symbol, BybitClient client, ILogger logger)
    {
        Symbol  = symbol;
        _client = client;
        _logger = logger;
    }

    public string Exchange => "Bybit";
    public string Symbol { get; }

    public Task<SymbolInfo> GetSymbolInfoAsync(CancellationToken ct) => _client.GetSymbolInfoAsync(Symbol, ct);

    public string ArchiveFileName(DateOnly day) => $"bybit-{Symbol}{day:yyyy-MM-dd}.csv.gz";

    public Task<bool> DownloadDayAsync(DateOnly day, string destination, CancellationToken ct) =>
        _client.DownloadDayAsync(Symbol, day, destination, ct);

    public IReadOnlyList<ExchangeTrade> ReadArchive(string path) => BybitClient.ReadArchive(path);

    public ITradeStream CreateStream(ILogger logger) => new BybitTradeStream(Symbol, logger);

    public async Task FillGapAsync(GapContext gap, CancellationToken ct)
    {
        var recent = await _client.GetRecentTradesAsync(Symbol, ct);
        if (recent.Count == 0) return;

        var empty  = gap.FromUnixUs == long.MinValue;
        var fromMs = empty ? gap.TodayStartMs : gap.FromUnixUs / 1000;

        // recent-trade reaches back to what we have: nothing was missed.
        if (recent[0].UnixUs / 1000 <= fromMs)
        {
            gap.WriteTrades(recent);
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // First whole minute not yet in the file (the last record's minute is partly there).
        var m0 = empty ? fromMs : (fromMs / MinuteMs + 1) * MinuteMs;
        m0 = Math.Max(m0, (nowMs - (long)MaxKlineGap.TotalMilliseconds) / MinuteMs * MinuteMs);
        // First minute boundary at/after the oldest trade recent-trade still has.
        var m1 = (recent[0].UnixUs / 1000 + MinuteMs - 1) / MinuteMs * MinuteMs;

        gap.MarkApproximate(fromMs, Math.Max(fromMs, m1 - 1));

        var tradesFromUs = long.MinValue;
        if (m1 > m0)
        {
            gap.Progress("Filling gap with minute bars");
            var bars = await _client.GetMinuteBarsAsync(Symbol, m0, m1 - MinuteMs, ct);
            gap.WriteBars(bars.Where(b => b.StartMs >= m0 && b.StartMs + MinuteMs <= m1 && b.Volume > 0).ToList());
            tradesFromUs = m1 * 1000;   // the last bar already covers recent trades before m1
            _logger.LogInformation("Bybit {Symbol}: gap {From:u} -> {To:u} filled with minute bars (repaired from the archive later)",
                Symbol, DateTimeOffset.FromUnixTimeMilliseconds(m0).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(m1).UtcDateTime);
        }
        gap.WriteTrades(recent.Where(t => t.UnixUs >= tradesFromUs).ToList());
    }
}
