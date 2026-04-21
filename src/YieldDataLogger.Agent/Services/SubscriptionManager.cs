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

    public event EventHandler<SubscriptionChange>? Changed;

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
            _logger.LogInformation("Loaded {Count} subscriptions from {Path}.", loaded.Symbols.Count, store.FilePath);
        }
        else
        {
            // First run on this machine: seed the file from appsettings so subsequent edits
            // from the Manager have something to start from.
            initial = options.Value.Symbols ?? Array.Empty<string>();
            _current = Normalize(initial).ToHashSet(StringComparer.Ordinal);
            try
            {
                store.Save(_current);
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
    public void UpdateTo(IEnumerable<string> symbols)
    {
        var next = Normalize(symbols).ToHashSet(StringComparer.Ordinal);

        SubscriptionChange change;
        lock (_gate)
        {
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

    private static IEnumerable<string> Normalize(IEnumerable<string> symbols) =>
        symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal);
}

/// <summary>Diff emitted on each subscription change. <paramref name="Current"/> is the full post-change set.</summary>
public sealed record SubscriptionChange(string[] Added, string[] Removed, string[] Current);
