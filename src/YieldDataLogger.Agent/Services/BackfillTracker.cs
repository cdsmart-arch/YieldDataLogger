namespace YieldDataLogger.Agent.Services;

/// <summary>
/// Thread-safe, lightweight store for the current backfill progress description.
/// HistoryBackfillService writes to it; StatusWriterService reads from it and
/// surfaces the value in the AgentStatus JSON file (which the Manager polls).
/// </summary>
public sealed class BackfillTracker
{
    private volatile string? _status;

    /// <summary>
    /// Current backfill progress, or null when no backfill is running.
    /// E.g. "Backfilling US10Y (3 of 20)…"
    /// </summary>
    public string? Status => _status;

    /// <summary>Update the progress message while a backfill is running.</summary>
    public void Update(string? message) => _status = message;

    /// <summary>Clear the status once the backfill finishes.</summary>
    public void Clear() => _status = null;
}
