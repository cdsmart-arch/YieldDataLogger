namespace YieldDataLogger.Api.Data.Entities;

/// <summary>
/// Tall-table row representing a single price observation for one instrument.
/// Keyed on (Symbol, TsUnix) so lookups for "history for X between A and B" stay cheap
/// regardless of how many instruments we track.
///
/// TsUnix is seconds-since-epoch (matches PriceTick.UnixTimeSeconds and the legacy
/// SqliteSink schema) - kept as a double so fractional seconds survive when we decide
/// to keep them.
/// </summary>
public sealed class PriceTickEntity
{
    public string Symbol { get; set; } = default!;
    public double TsUnix { get; set; }
    public double Price { get; set; }
    public string Source { get; set; } = default!;
}
