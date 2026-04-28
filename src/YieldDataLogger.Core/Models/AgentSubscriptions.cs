namespace YieldDataLogger.Core.Models;

/// <summary>
/// Persisted subscription list for a single Agent installation. Written by the Manager
/// (end user picking instruments) and read / file-watched by the Agent (which applies the
/// diff against the live SignalR hub connection). Living in <see cref="Core"/> keeps the
/// contract in one place - both sides deserialise it with the same <c>System.Text.Json</c>
/// camelCase options.
///
/// Semantics: the symbol list is authoritative - what the Agent should be subscribed to
/// right now. Added symbols produce Subscribe calls, removed ones produce Unsubscribe calls.
/// </summary>
public sealed record AgentSubscriptions
{
    /// <summary>Schema version; bump if the contract changes incompatibly.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Canonical symbol list the Agent should subscribe to.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = Array.Empty<string>();

    /// <summary>
    /// How many days of history to backfill from Azure on a fresh install or after a gap.
    /// 0 means "use the agent default from appsettings.json" (currently 30 days).
    /// Set by the Manager's Symbols window so end-users never have to edit a config file.
    /// </summary>
    public int HistoryDays { get; init; } = 0;

    /// <summary>
    /// Milliseconds to pause between backfilling consecutive symbols.
    /// 0 means "use the agent default from appsettings.json" (currently 300 ms).
    /// Set to a low value (e.g. 50) for a fast first-run fill, or higher (500+) to keep
    /// the machine responsive while downloading large histories in the background.
    /// </summary>
    public int BackfillDelayMs { get; init; } = 0;

    /// <summary>UTC timestamp of the most recent edit. Useful for diagnostics / "last saved".</summary>
    public DateTime UpdatedAtUtc { get; init; }
}
