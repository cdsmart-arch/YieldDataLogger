using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace YieldDataLogger.ExchangeFeed.Feeds;

/// <summary>
/// Reconnecting WebSocket trade stream. Never gives up: any error, a server close, silence
/// longer than <see cref="SilenceLimit"/> or a connection older than <see cref="MaxConnectionAge"/>
/// reconnects with backoff (1 s doubling to 30 s, reset once a connection is confirmed).
/// Protocol-level pings from the server are answered by ClientWebSocket itself.
/// </summary>
public abstract class WebSocketTradeStream : ITradeStream
{
    protected WebSocketTradeStream(string symbol, ILogger logger)
    {
        Symbol = symbol;
        Logger = logger;
    }

    protected string Symbol { get; }
    protected ILogger Logger { get; }

    public bool Connected { get; private set; }
    public string? LastError { get; private set; }

    protected abstract string Exchange { get; }
    protected abstract Uri Endpoint { get; }
    protected virtual TimeSpan SilenceLimit => TimeSpan.FromSeconds(60);
    protected virtual TimeSpan MaxConnectionAge => TimeSpan.FromHours(23);

    /// <summary>Application-level keep-alive message and interval; null = none.</summary>
    protected virtual (string Message, TimeSpan Every)? KeepAlive => null;

    /// <summary>Sent right after connecting (e.g. a subscribe request).</summary>
    protected virtual Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct) => Task.CompletedTask;

    /// <summary>True when the connection counts as live as soon as it opens (no subscribe ack to wait for).</summary>
    protected virtual bool LiveOnOpen => false;

    /// <summary>Parses one message. Return trades, or set <paramref name="confirmed"/> when a subscribe ack arrives.</summary>
    protected abstract IReadOnlyList<ExchangeTrade>? Parse(JsonElement root, out bool confirmed);

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
                Logger.LogWarning("{Exchange} WebSocket {Symbol}: {Error} - reconnecting in {Delay}s",
                    Exchange, Symbol, ex.Message, backoff.TotalSeconds);
            }
            finally { Connected = false; }

            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
        }
    }

    private async Task RunConnectionAsync(ChannelWriter<FeedItem> output, CancellationToken ct, Action resetBackoff)
    {
        using var ws = new ClientWebSocket();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(Endpoint, connectCts.Token);
        }
        await OnConnectedAsync(ws, ct);

        void Confirm()
        {
            Connected = true;
            LastError = null;
            resetBackoff();
            Logger.LogInformation("{Exchange} WebSocket {Symbol}: live", Exchange, Symbol);
            output.TryWrite(FeedItem.ConnectedMarker);
        }
        if (LiveOnOpen) Confirm();

        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = KeepAlive is { } ka ? PingLoopAsync(ws, ka.Message, ka.Every, connCts.Token) : Task.CompletedTask;
        var openedAt = DateTime.UtcNow;
        try
        {
            var buffer = new ArrayBufferWriter<byte>(64 * 1024);
            while (true)
            {
                if (DateTime.UtcNow - openedAt > MaxConnectionAge)
                    throw new TimeoutException("planned reconnect before the exchange's 24h connection limit");

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

                using var doc = JsonDocument.Parse(buffer.WrittenMemory);
                var trades = Parse(doc.RootElement, out var confirmed);
                if (confirmed) Confirm();
                if (trades is { Count: > 0 }) output.TryWrite(new FeedItem(trades, false));
            }
        }
        finally
        {
            connCts.Cancel();
            try { await pinger; } catch { /* cancelled */ }
        }
    }

    private static async Task PingLoopAsync(ClientWebSocket ws, string message, TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(every, ct);
            await SendAsync(ws, message, ct);
        }
    }

    protected static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
}
