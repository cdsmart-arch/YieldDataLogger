using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Pipeline;

/// <summary>
/// Single fan-out point between the CNBC/Investing sources and all registered sinks.
/// Sources call <see cref="DispatchAsync"/>; the dispatcher writes to every enabled sink
/// and swallows per-sink failures so one bad sink can't kill the pipeline.
///
/// Deduplication: consecutive identical prices for the same symbol are dropped here,
/// so every sink downstream (Azure Table, SQLite, .scid, SignalR push, ...) only ever
/// sees actual price changes. Matches the original ProcessPoller behaviour where
/// only changed yields were persisted.
/// </summary>
public sealed class TickDispatcher
{
    private readonly IReadOnlyList<IPriceSink> _sinks;
    private readonly ILogger<TickDispatcher> _logger;
    private readonly ConcurrentDictionary<string, double> _lastPriceBySymbol = new(StringComparer.Ordinal);
    private long _ticksReceived;
    private long _ticksDeduped;
    private long _ticksDispatched;

    public TickDispatcher(IEnumerable<IPriceSink> sinks, ILogger<TickDispatcher> logger)
    {
        _sinks = sinks.ToList();
        _logger = logger;
        _logger.LogInformation("TickDispatcher wired to sinks: {Sinks}",
            string.Join(", ", _sinks.Select(s => s.Name)));
    }

    public long TicksReceived  => Interlocked.Read(ref _ticksReceived);
    public long TicksDeduped   => Interlocked.Read(ref _ticksDeduped);
    public long TicksDispatched => Interlocked.Read(ref _ticksDispatched);

    public async ValueTask DispatchAsync(PriceTick tick, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _ticksReceived);

        // Drop consecutive identical prices per symbol. Strict equality is fine here:
        // the dedup is applied to raw doubles from a single source per symbol, so matching
        // prices compare bit-exact.
        if (_lastPriceBySymbol.TryGetValue(tick.CanonicalSymbol, out var last) && last == tick.Price)
        {
            Interlocked.Increment(ref _ticksDeduped);
            return;
        }
        _lastPriceBySymbol[tick.CanonicalSymbol] = tick.Price;

        Interlocked.Increment(ref _ticksDispatched);
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(tick, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sink {Sink} threw on {Symbol}", sink.Name, tick.CanonicalSymbol);
            }
        }
    }
}
