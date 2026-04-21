using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using SocketIO.Core;
using SocketIOClient;
using YieldDataLogger.Collector.Configuration;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Core.Models;

using SioClient = SocketIOClient.SocketIO;

namespace YieldDataLogger.Collector.Sources;

/// <summary>
/// Direct Socket.IO client against investing.com's forexpros stream endpoint.
/// Replaces the old Selenium + local HTML hack in CreateHTMLQuery/ProcessLog.
///
/// The investing bundle (main-1.17.55.min.js) does its own wrapping on top of Socket.IO.
/// Its wire format is: subscribe to pid rooms ("pid-XXXX:"), receive messages whose payload
/// looks like { pid, last_numeric, timestamp, ... }. We try to speak that directly from C#.
///
/// If the remote server has moved to an unsupported protocol version, the dispatcher will
/// observe no ticks and the containing hosted service can switch to the Playwright fallback.
/// </summary>
public sealed class InvestingSocketClient : IInvestingStreamClient
{
    private readonly IOptionsMonitor<CollectorOptions> _options;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<InvestingSocketClient> _logger;
    private SioClient? _socket;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private bool _subscribedAtLeastOne;

    public event Func<PriceTick, CancellationToken, ValueTask>? OnTick;

    public InvestingSocketClient(
        IOptionsMonitor<CollectorOptions> options,
        InstrumentCatalog catalog,
        ILogger<InvestingSocketClient> logger)
    {
        _options = options;
        _catalog = catalog;
        _logger = logger;
    }

    public bool IsLive => _socket?.Connected == true && _subscribedAtLeastOne;
    public DateTime LastTickUtc => _lastTickUtc;

    public async Task StartAsync(IEnumerable<int> pids, CancellationToken ct)
    {
        var opts = _options.CurrentValue.Investing;
        var pidList = pids.Distinct().ToList();
        if (pidList.Count == 0)
        {
            _logger.LogWarning("InvestingSocketClient.StartAsync called with no pids; nothing to subscribe.");
            return;
        }

        _socket = new SioClient(opts.StreamUrl, new SocketIOOptions
        {
            EIO = opts.EngineIoVersion == 4 ? EngineIO.V4 : EngineIO.V3,
            Reconnection = true,
            ReconnectionAttempts = int.MaxValue,
            ReconnectionDelay = opts.MinReconnectMs,
            ReconnectionDelayMax = opts.MaxReconnectMs,
            ConnectionTimeout = TimeSpan.FromSeconds(20),
        });

        _socket.OnConnected += async (_, _) =>
        {
            _logger.LogInformation("Investing socket connected to {Url}, subscribing to {Count} pids",
                opts.StreamUrl, pidList.Count);
            await SubscribePidsAsync(pidList).ConfigureAwait(false);
        };

        _socket.OnDisconnected += (_, reason) =>
        {
            _logger.LogWarning("Investing socket disconnected: {Reason}", reason);
            _subscribedAtLeastOne = false;
        };

        _socket.OnError += (_, err) =>
            _logger.LogWarning("Investing socket error: {Error}", err);

        _socket.OnReconnectAttempt += (_, attempt) =>
            _logger.LogInformation("Investing socket reconnect attempt #{Attempt}", attempt);

        // The bundle emits messages addressed to each pid room. We attach a catch-all handler
        // to "message" and let the payload-shape detector sort out what is and isn't a tick.
        _socket.OnAny(async (eventName, response) =>
        {
            try
            {
                await HandleSocketMessageAsync(eventName, response, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Investing socket message handler threw");
            }
        });

        await _socket.ConnectAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_socket is null) return;
        try { await _socket.DisconnectAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is not null)
        {
            try { await _socket.DisconnectAsync().ConfigureAwait(false); } catch { }
            _socket.Dispose();
            _socket = null;
        }
    }

    private async Task SubscribePidsAsync(IReadOnlyList<int> pids)
    {
        if (_socket is null) return;

        // Investing's jQuery bundle uses a custom window-event named "socketRetry" whose payload
        // is an array of room names shaped "pid-XXXX:". When replayed over raw Socket.IO the
        // closest equivalent is an emit with that event name and payload. If the remote side
        // does not honour this shape we'll see it in the logs (no inbound messages) and swap to
        // the Playwright fallback.
        var rooms = pids.Select(p => $"pid-{p}:").ToArray();
        try
        {
            await _socket.EmitAsync("socketRetry", (object)rooms).ConfigureAwait(false);
            _subscribedAtLeastOne = true;
            _logger.LogInformation("Emitted socketRetry for {Count} rooms", rooms.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit socketRetry");
        }
    }

    private async ValueTask HandleSocketMessageAsync(string eventName, SocketIOResponse response, CancellationToken ct)
    {
        // The vendor's wrapper used to surface ticks as a "socketMessage" window event with
        // payload { pid, last_numeric, timestamp }. The underlying socket may deliver them under
        // various event names - we sniff any payload that looks tick-shaped and accept it.
        JToken? payload = null;
        try
        {
            payload = JToken.Parse(response.ToString());
        }
        catch
        {
            return;
        }

        // Unwrap one-element arrays that Socket.IO likes to deliver.
        if (payload is JArray arr && arr.Count > 0) payload = arr[0];
        if (payload is not JObject obj) return;

        var pidToken = obj["pid"] ?? obj["instrument_id"] ?? obj["id"];
        var priceToken = obj["last_numeric"] ?? obj["last"] ?? obj["price"];
        if (pidToken is null || priceToken is null) return;

        if (!int.TryParse(pidToken.ToString(), out var pid)) return;
        if (!double.TryParse(priceToken.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var price)) return;

        if (!_catalog.ByPid.TryGetValue(pid, out var instr)) return;

        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _lastTickUtc = DateTime.UtcNow;

        var tick = new PriceTick(instr.CanonicalSymbol, unix, price, "investing");
        var handler = OnTick;
        if (handler is not null)
            await handler(tick, ct).ConfigureAwait(false);

        _logger.LogTrace("Investing tick {Event} {Symbol} {Price}", eventName, instr.CanonicalSymbol, price);
    }
}
