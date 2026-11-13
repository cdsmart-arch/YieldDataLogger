using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Agent.Configuration;

namespace YieldDataLogger.Agent.Services;

/// <summary>
/// In-process source of truth for the Agent's current subscription set. Loads from the
/// <see cref="SubscriptionStore"/> on construction (or seeds from
/// <see cref="AgentOptions.Symbols"/> on first run), then exposes <see cref="UpdateTo"/>
/// and a <see cref="Changed"/> event so the <see cref="TickHubClient"/> can apply diffs to
/// the live hub connection.
///
/// Thread-safety: all mutations happen via <see cref="UpdateTo"/>, which serialises under
/// <see cref="_gate"/>. Readers get a defensive copy.
/// </summary>
public sealed class SubscriptionManager
{
    private readonly SubscriptionStore _store;
    private readonly ILogger<SubscriptionManager> _logger;
    private readonly object _gate = new();
    private HashSet<string> _current = new(StringComparer.Ordinal);
    private int _historyDays;
    private int _backfillDelayMs;

    private ScidSettings _scid = new(null, null);

    public event EventHandler<SubscriptionChange>? Changed;

    /// <summary>Raised when the Manager changes the Sierra folder or the .scid symbol set.</summary>
    public event EventHandler<ScidSettings>? ScidChanged;

    /// <summary>Sierra .scid settings from subscriptions.json (nulls = fall back to appsettings).</summary>
    public ScidSettings Scid { get { lock (_gate) return _scid; } }

    /// <summary>Days of history to backfill (0 = use appsettings default).</summary>
    public int HistoryDays    { get { lock (_gate) return _historyDays;    } }

    /// <summary>Milliseconds to pause between symbols during backfill (0 = use appsettings default).</summary>
    public int BackfillDelayMs { get { lock (_gate) return _backfillDelayMs; } }

    public SubscriptionManager(
        IOptions<AgentOptions> options,
        SubscriptionStore store,
        ILogger<SubscriptionManager> logger)
    {
        _store = store;
        _logger = logger;

        var loaded = store.Load();
        IEnumerable<string> initial;
        if (loaded is not null)
        {
            initial = loaded.Symbols;
            _historyDays    = loaded.HistoryDays;
            _backfillDelayMs = loaded.BackfillDelayMs;
            _scid = ScidSettings.From(loaded);
            _logger.LogInformation(
                "Loaded {Count} subscriptions from {Path} (historyDays={Days}, backfillDelayMs={Delay}).",
                loaded.Symbols.Count, store.FilePath, _historyDays, _backfillDelayMs);
        }
        else
        {
            // First run on this machine: seed the file from appsettings so subsequent edits
            // from the Manager have something to start from.
            initial = options.Value.Symbols ?? Array.Empty<string>();
            _current = Normalize(initial).ToHashSet(StringComparer.Ordinal);
            try
            {
                store.Save(_current, historyDays: 0);
                _logger.LogInformation("Seeded {Path} with {Count} symbols from appsettings.", store.FilePath, _current.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not seed subscriptions file at {Path}.", store.FilePath);
            }
            return;
        }

        _current = Normalize(initial).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Current
    {
        get
        {
            lock (_gate) return _current.ToArray();
        }
    }

    /// <summary>
    /// Replaces the subscription set with <paramref name="symbols"/>. Raises <see cref="Changed"/>
    /// with the diff (added, removed, final set) if anything actually moved. Persistence is
    /// the caller's responsibility - when the Manager writes the file the watcher invokes
    /// this, and when the Agent's seed path calls the store directly, no persistence is needed.
    /// </summary>
    public void UpdateTo(IEnumerable<string> symbols, int historyDays = 0, int backfillDelayMs = 0)
    {
        var next = Normalize(symbols).ToHashSet(StringComparer.Ordinal);

        SubscriptionChange change;
        lock (_gate)
        {
            _historyDays     = historyDays;
            _backfillDelayMs = backfillDelayMs;
            var added   = next.Except(_current).ToArray();
            var removed = _current.Except(next).ToArray();
            if (added.Length == 0 && removed.Length == 0) return;
            _current = next;
            change = new SubscriptionChange(added, removed, next.ToArray());
        }

        _logger.LogInformation(
            "Subscriptions updated: +{Added} -{Removed}; current={Total}.",
            change.Added.Length, change.Removed.Length, change.Current.Length);

        Changed?.Invoke(this, change);
    }

    /// <summary>Applies Sierra .scid settings; raises <see cref="ScidChanged"/> only on a real change.</summary>
    public void UpdateScid(ScidSettings next)
    {
        lock (_gate)
        {
            if (_scid.SameAs(next)) return;
            _scid = next;
        }
        _logger.LogInformation("Sierra settings updated: folder={Folder} symbols={Symbols}.",
            next.Folder ?? "(appsettings)",
            next.Symbols is null ? "(appsettings)" : string.Join(",", next.Symbols));
        ScidChanged?.Invoke(this, next);
    }

    private static IEnumerable<string> Normalize(IEnumerable<string> symbols) =>
        symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal);
}

/// <summary>Diff emitted on each subscription change. <paramref name="Current"/> is the full post-change set.</summary>
public sealed record SubscriptionChange(string[] Added, string[] Removed, string[] Current);

/// <summary>
/// Sierra .scid settings as persisted by the Manager. <c>null</c> members mean "not set in
/// subscriptions.json - use the appsettings defaults".
/// </summary>
public sealed record ScidSettings(string? Folder, IReadOnlyList<string>? Symbols)
{
    public static ScidSettings From(YieldDataLogger.Core.Models.AgentSubscriptions s) => new(
        string.IsNullOrWhiteSpace(s.ScidFolder) ? null : s.ScidFolder.Trim(),
        s.ScidSymbols?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray());

    public bool SameAs(ScidSettings other) =>
        string.Equals(Folder, other.Folder, StringComparison.OrdinalIgnoreCase)
        && (Symbols is null ? other.Symbols is null
            : other.Symbols is not null && Symbols.SequenceEqual(other.Symbols, StringComparer.Ordinal));
}
