namespace YieldDataLogger.Core.Models;

/// <summary>
/// A single price observation for a canonical symbol.
/// Timestamp is unix epoch seconds (UTC); matches the existing SQLite schema's TIMESTAMP column.
/// </summary>
public readonly record struct PriceTick(string CanonicalSymbol, double UnixTimeSeconds, double Price, string Source);
