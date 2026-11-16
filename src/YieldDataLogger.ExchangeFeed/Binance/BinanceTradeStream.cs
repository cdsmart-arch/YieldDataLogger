using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Binance;

/// <summary>
/// Binance USDⓈ-M aggTrade stream. Since the 2026 routing change aggTrade is served under
/// /market (the legacy /ws path no longer carries it). No subscribe message: the stream is in
/// the URL, so the connection is live once open. Binance sends protocol pings (answered by
/// ClientWebSocket) and drops connections at 24 h, so we reconnect at 23 h. Quiet contracts
/// can go minutes without a trade, so the silence limit is generous.
/// </summary>
public sealed class BinanceTradeStream : WebSocketTradeStream
{
    public BinanceTradeStream(string symbol, ILogger logger) : base(symbol, logger)
    {
        Endpoint = new Uri($"wss://fstream.binance.com/market/ws/{symbol.ToLowerInvariant()}@aggTrade");
    }

    protected override string Exchange => "Binance";
    protected override Uri Endpoint { get; }
    protected override bool LiveOnOpen => true;
    protected override TimeSpan SilenceLimit => TimeSpan.FromMinutes(10);

    protected override IReadOnlyList<ExchangeTrade>? Parse(JsonElement root, out bool confirmed)
    {
        confirmed = false;
        if (!root.TryGetProperty("e", out var e) || e.GetString() != "aggTrade") return null;
        var id = root.GetProperty("a").GetInt64();
        return new[]
        {
            new ExchangeTrade(
                root.GetProperty("T").GetInt64() * 1000,
                double.Parse(root.GetProperty("p").GetString()!, CultureInfo.InvariantCulture),
                double.Parse(root.GetProperty("q").GetString()!, CultureInfo.InvariantCulture),
                IsBuy: !root.GetProperty("m").GetBoolean(),
                id.ToString(CultureInfo.InvariantCulture),
                BinanceClient.Fills(root.GetProperty("f").GetInt64(), root.GetProperty("l").GetInt64())),
        };
    }
}
