using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Pipeline;

/// <summary>
/// Single fan-out point between the CNBC/Investing sources and all registered sinks.
/// Sources call <see cref="DispatchAsync"/>; the dispatcher writes to every enabled sink
/// and swallows per-sink failures so one bad sink can't kill the pipeline.
/// </summary>
public sealed class TickDispatcher
{
    private readonly IReadOnlyList<IPriceSink> _sinks;
    private readonly ILogger<TickDispatcher> _logger;
    private long _ticksReceived;
    private long _ticksWritten;

    public TickDispatcher(IEnumerable<IPriceSink> sinks, ILogger<TickDispatcher> logger)
    {
        _sinks = sinks.ToList();
        _logger = logger;
        _logger.LogInformation("TickDispatcher wired to sinks: {Sinks}",
            string.Join(", ", _sinks.Select(s => s.Name)));
    }

    public long TicksReceived => Interlocked.Read(ref _ticksReceived);
    public long TicksWritten => Interlocked.Read(ref _ticksWritten);

    public async ValueTask DispatchAsync(PriceTick tick, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _ticksReceived);
        foreach (var sink in _sinks)
        {
            try
            {
                await sink.WriteAsync(tick, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _ticksWritten);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sink {Sink} threw on {Symbol}", sink.Name, tick.CanonicalSymbol);
            }
        }
    }
}
