using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Core.Abstractions;

/// <summary>
/// A downstream consumer of price ticks. Implementations include the SQLite writer
/// (used by the NT-flavoured local agent) and the Sierra .scid writer.
/// </summary>
public interface IPriceSink
{
    string Name { get; }
    ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default);
}
