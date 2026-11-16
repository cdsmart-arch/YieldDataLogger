using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace YieldDataLogger.ExchangeFeed.Feeds;

/// <summary>
/// One exchange trade. <see cref="UnixUs"/> is µs since the Unix epoch. <see cref="IsBuy"/>
/// is the taker's side (true = an aggressive buyer lifted the ask). <see cref="Fills"/> is
/// the number of underlying fills (Binance aggTrades bundle several; Bybit trades are 1).
/// </summary>
public readonly record struct ExchangeTrade(long UnixUs, double Price, double Size, bool IsBuy, string Id, uint Fills = 1);

/// <summary>A one-minute bar used to approximate a gap when the exchange can't supply trades.</summary>
public readonly record struct GapBar(long StartMs, double Open, double High, double Low, double Close, double Volume);

/// <summary>Static facts about a contract.</summary>
public sealed record SymbolInfo(DateOnly LaunchDay, double QuantityStep);

/// <summary>Item on a feed's live queue: a batch of trades, or a marker that the stream (re)connected.</summary>
public sealed record FeedItem(IReadOnlyList<ExchangeTrade>? Trades, bool Connected)
{
    public static readonly FeedItem ConnectedMarker = new(null, true);
}

/// <summary>A live trade stream. Must never give up; queues <see cref="FeedItem.ConnectedMarker"/> after every (re)connect.</summary>
public interface ITradeStream
{
    bool Connected { get; }
    string? LastError { get; }
    Task RunAsync(ChannelWriter<FeedItem> output, CancellationToken ct);
}

/// <summary>
/// What a gap fill may do. Trades and bars go through the feed (dedupe, ascending order);
/// <see cref="MarkApproximate"/> flags a time range whose data is incomplete or sideless so the
/// feed replaces those days from the archive once it's published.
/// </summary>
public sealed class GapContext
{
    /// <summary>Newest trade already in the file (Unix µs), or <see cref="long.MinValue"/> when the file is empty.</summary>
    public required long FromUnixUs { get; init; }
    public required long TodayStartMs { get; init; }
    public required Action<IReadOnlyList<ExchangeTrade>> WriteTrades { get; init; }
    public required Action<IReadOnlyList<GapBar>> WriteBars { get; init; }
    public required Action<long, long> MarkApproximate { get; init; }
    public required Action<string> Progress { get; init; }
}

/// <summary>
/// Everything exchange-specific about one symbol. The generic <see cref="TradeFeed"/> does the
/// rest: file, state, history, repair, dedupe and the live loop.
/// </summary>
public interface IExchangeSource
{
    /// <summary>Display name, also the .scid suffix (upper-cased): Bybit -> _XAUUSDT_BYBIT.scid.</summary>
    string Exchange { get; }
    string Symbol { get; }

    Task<SymbolInfo> GetSymbolInfoAsync(CancellationToken ct);

    /// <summary>File name the day's archive is cached under.</summary>
    string ArchiveFileName(DateOnly day);

    /// <summary>Downloads one UTC day's trade archive. False when the exchange has none (yet) for that day.</summary>
    Task<bool> DownloadDayAsync(DateOnly day, string destination, CancellationToken ct);

    /// <summary>All trades in a downloaded archive, in time order.</summary>
    IReadOnlyList<ExchangeTrade> ReadArchive(string path);

    ITradeStream CreateStream(ILogger logger);

    /// <summary>Brings the file from <see cref="GapContext.FromUnixUs"/> up to now via REST.</summary>
    Task FillGapAsync(GapContext gap, CancellationToken ct);
}
