namespace YieldDataLogger.Core.Models;

/// <summary>
/// Canonical representation of a tradable instrument tracked by the collector.
/// Sourced from investing.com pids and/or cnbc symbols, unified under a single
/// CanonicalSymbol used for local storage and downstream distribution.
/// </summary>
public sealed class Instrument
{
    /// <summary>investing.com pid; null when the instrument is CNBC-only.</summary>
    public int? InvestingPid { get; init; }

    /// <summary>CNBC quick-quote symbol; null when the instrument is investing-only.</summary>
    public string? CnbcSymbol { get; init; }

    /// <summary>Canonical in-house ticker (e.g. "US10Y", "DE02Y"). Unique per instrument.</summary>
    public required string CanonicalSymbol { get; init; }

    public string? Category { get; init; }
}
