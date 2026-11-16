using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Bybit;

/// <summary>
/// Bybit v5 linear public trades. Subscribes to publicTrade.{SYMBOL}; the connection counts as
/// live once the subscribe is acknowledged. Bybit wants an application ping about every 20 s.
/// </summary>
public sealed class BybitTradeStream : WebSocketTradeStream
{
    public BybitTradeStream(string symbol, ILogger logger) : base(symbol, logger) { }

    protected override string Exchange => "Bybit";
    protected override Uri Endpoint { get; } = new("wss://stream.bybit.com/v5/public/linear");
    protected override (string Message, TimeSpan Every)? KeepAlive => ("{\"op\":\"ping\"}", TimeSpan.FromSeconds(20));

    protected override Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct) =>
        SendAsync(ws, $"{{\"op\":\"subscribe\",\"args\":[\"publicTrade.{Symbol}\"]}}", ct);

    protected override IReadOnlyList<ExchangeTrade>? Parse(JsonElement root, out bool confirmed)
    {
        confirmed = false;
        if (root.TryGetProperty("op", out var op))
        {
            if (op.GetString() == "subscribe")
            {
                if (root.TryGetProperty("success", out var ok) && ok.GetBoolean()) confirmed = true;
                else throw new WebSocketException($"subscribe rejected: {root.GetRawText()}");
            }
            return null; // pong etc.
        }

        if (!root.TryGetProperty("topic", out var topic) || topic.GetString() != $"publicTrade.{Symbol}") return null;
        if (!root.TryGetProperty("data", out var data)) return null;

        var trades = new List<ExchangeTrade>(data.GetArrayLength());
        foreach (var t in data.EnumerateArray())
        {
            trades.Add(new ExchangeTrade(
                t.GetProperty("T").GetInt64() * 1000,
                double.Parse(t.GetProperty("p").GetString()!, CultureInfo.InvariantCulture),
                double.Parse(t.GetProperty("v").GetString()!, CultureInfo.InvariantCulture),
                t.GetProperty("S").GetString() == "Buy",
                t.GetProperty("i").GetString()!));
        }
        return trades;
    }
}
