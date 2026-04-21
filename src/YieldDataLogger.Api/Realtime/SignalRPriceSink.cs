using Microsoft.AspNetCore.SignalR;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Realtime;

/// <summary>
/// Fans out every <see cref="PriceTick"/> produced by the collector to the SignalR
/// group matching the tick's canonical symbol. Failures are swallowed so that a
/// slow/broken client never stalls the ingest pipeline.
/// </summary>
public sealed class SignalRPriceSink : IPriceSink
{
    private readonly IHubContext<TicksHub> _hub;
    private readonly ILogger<SignalRPriceSink> _logger;

    public string Name => "signalr";

    public SignalRPriceSink(IHubContext<TicksHub> hub, ILogger<SignalRPriceSink> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        try
        {
            var symbol = (tick.CanonicalSymbol ?? string.Empty).ToUpperInvariant();
            if (symbol.Length == 0) return;

            var payload = new TickPayload(symbol, tick.UnixTimeSeconds, tick.Price, tick.Source);
            await _hub.Clients.Group(symbol).SendAsync("Tick", payload, ct);
        }
        catch (OperationCanceledException)
        {
            // shutdown - nothing to do
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SignalR push failed for {Symbol}", tick.CanonicalSymbol);
        }
    }

    /// <summary>
    /// Shape of the message delivered to clients via the "Tick" hub method.
    /// Property names are serialised as camelCase (symbol, tsUnix, price, source)
    /// to match the REST DTO.
    /// </summary>
    private sealed record TickPayload(string Symbol, double TsUnix, double Price, string Source);
}
