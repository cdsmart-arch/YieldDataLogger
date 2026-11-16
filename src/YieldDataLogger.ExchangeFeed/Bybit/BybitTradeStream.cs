using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace YieldDataLogger.ExchangeFeed.Bybit;

/// <summary>Item on a feed's live queue: a batch of trades, or a marker that a (re)subscribe just succeeded.</summary>
public sealed record FeedItem(IReadOnlyList<BybitTrade>? Trades, bool Subscribed)
{
    public static readonly FeedItem SubscribedMarker = new(null, true);
}

/// <summary>
/// Live public trades for one symbol from Bybit's v5 linear WebSocket. Never gives up: on any
/// error or a silent connection (no message for 60 s) it reconnects with backoff. After every
/// successful subscribe it queues <see cref="FeedItem.SubscribedMarker"/> so the feed can fill
/// whatever it missed while disconnected.
/// </summary>
public sealed class BybitTradeStream
{
    private static readonly Uri Endpoint = new("wss://stream.bybit.com/v5/public/linear");
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);

    private readonly string _symbol;
    private readonly ILogger _logger;

    public BybitTradeStream(string symbol, ILogger logger)
    {
        _symbol = symbol;
        _logger = logger;
    }

    public bool Connected { get; private set; }
    public string? LastError { get; private set; }

    public async Task RunAsync(ChannelWriter<FeedItem> output, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(output, ct, () => backoff = TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger.LogWarning("Bybit WebSocket {Symbol}: {Error} - reconnecting in {Delay}s",
                    _symbol, ex.Message, backoff.TotalSeconds);
            }
            finally { Connected = false; }

            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    private async Task RunConnectionAsync(ChannelWriter<FeedItem> output, CancellationToken ct, Action onSubscribed)
    {
        using var ws = new ClientWebSocket();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(Endpoint, connectCts.Token);
        }
        await SendAsync(ws, $"{{\"op\":\"subscribe\",\"args\":[\"publicTrade.{_symbol}\"]}}", ct);

        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = PingLoopAsync(ws, connCts.Token);
        try
        {
            var buffer = new ArrayBufferWriter<byte>(64 * 1024);
            while (true)
            {
                buffer.Clear();
                using (var silence = CancellationTokenSource.CreateLinkedTokenSource(connCts.Token))
                {
                    silence.CancelAfter(SilenceLimit);
                    ValueWebSocketReceiveResult r;
                    do
                    {
                        try
                        {
                            r = await ws.ReceiveAsync(buffer.GetMemory(16 * 1024), silence.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException($"no message for {SilenceLimit.TotalSeconds:0}s");
                        }
                        if (r.MessageType == WebSocketMessageType.Close)
                            throw new WebSocketException($"server closed the connection ({ws.CloseStatusDescription})");
                        buffer.Advance(r.Count);
                    } while (!r.EndOfMessage);
                }
                Handle(buffer.WrittenSpan, output, onSubscribed);
            }
        }
        finally
        {
            connCts.Cancel();
            try { await pinger; } catch { /* cancelled */ }
        }
    }

    private void Handle(ReadOnlySpan<byte> json, ChannelWriter<FeedItem> output, Action onSubscribed)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;

        if (root.TryGetProperty("op", out var op))
        {
            if (op.GetString() == "subscribe")
            {
                if (root.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    Connected = true;
                    LastError = null;
                    onSubscribed();
                    _logger.LogInformation("Bybit WebSocket {Symbol}: subscribed", _symbol);
                    output.TryWrite(FeedItem.SubscribedMarker);
                }
                else
                {
                    throw new WebSocketException($"subscribe rejected: {root.GetRawText()}");
                }
            }
            return; // pong etc.
        }

        if (!root.TryGetProperty("topic", out var topic) || topic.GetString() != $"publicTrade.{_symbol}") return;
        if (!root.TryGetProperty("data", out var data)) return;

        var trades = new List<BybitTrade>(data.GetArrayLength());
        foreach (var t in data.EnumerateArray())
        {
            trades.Add(new BybitTrade(
                t.GetProperty("T").GetInt64() * 1000,
                double.Parse(t.GetProperty("p").GetString()!, CultureInfo.InvariantCulture),
                double.Parse(t.GetProperty("v").GetString()!, CultureInfo.InvariantCulture),
                t.GetProperty("S").GetString() == "Buy",
                t.GetProperty("i").GetString()!));
        }
        if (trades.Count > 0) output.TryWrite(new FeedItem(trades, false));
    }

    private static async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(PingEvery, ct);
            await SendAsync(ws, "{\"op\":\"ping\"}", ct);
        }
    }

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
}
