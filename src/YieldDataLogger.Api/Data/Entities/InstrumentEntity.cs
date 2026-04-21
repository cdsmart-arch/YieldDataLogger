namespace YieldDataLogger.Api.Data.Entities;

/// <summary>
/// Mirrors the InstrumentCatalog rows. The authoritative copy still lives in instruments.json
/// on disk (so Phase 4 agents and the Manager WPF app can operate offline); the DB copy is a
/// read-optimised view for REST clients. Admin mutations update both.
/// </summary>
public sealed class InstrumentEntity
{
    public string CanonicalSymbol { get; set; } = default!;
    public int? InvestingPid { get; set; }
    public string? CnbcSymbol { get; set; }
    public string? Category { get; set; }
}
