using System.Collections.Concurrent;
using System.Data.SQLite;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Core.Sinks;

/// <summary>
/// Writes each tick to a per-instrument SQLite file ({symbol}.sqlite) matching the
/// schema used by the original YieldLoggerUI app: PriceData(TIMESTAMP real, CLOSE real).
/// Kept compatible on purpose so the NT8 indicator and any other legacy reader can
/// consume the same files without changes.
///
/// Performance notes
/// -----------------
/// • One SQLiteConnection is opened per symbol and kept alive for the process lifetime.
///   This eliminates the open/close overhead that would otherwise occur on every tick.
/// • WAL journal mode is enabled on first open (persisted in the file header so all
///   future connections, including the NT8 indicator, also benefit automatically).
/// • SYNCHRONOUS=NORMAL removes the full-fsync on every write while still protecting
///   against data loss on OS crashes (not power-loss, which is acceptable here).
/// • A 4 MB page cache reduces read-back I/O during index lookups.
///
/// Dedup is handled centrally by TickDispatcher, so this sink only ever sees ticks
/// whose price has actually changed.
/// </summary>
public sealed class SqliteSink : IPriceSink, IDisposable
{
    private readonly string _directory;
    private readonly ILogger<SqliteSink> _logger;
    private readonly ConcurrentDictionary<string, SQLiteConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _initLock = new();

    public string Name => "sqlite";

    public SqliteSink(string directory, ILogger<SqliteSink> logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger    = logger;
        Directory.CreateDirectory(_directory);
    }

    public ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        try
        {
            var cn = GetOrOpenConnection(tick.CanonicalSymbol);
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO PriceData (TIMESTAMP, CLOSE) VALUES (@TS, @CLOSE)";
            cmd.Parameters.AddWithValue("@TS",    tick.UnixTimeSeconds);
            cmd.Parameters.AddWithValue("@CLOSE", tick.Price);
            cmd.ExecuteNonQuery();

            // Checkpoint WAL back into the main .sqlite file after every write.
            // PASSIVE mode is non-blocking — it only checkpoints pages that no reader
            // is currently accessing.  This keeps the main file's mtime current so
            // the NinjaTrader FileSystemWatcher fires on *.sqlite reliably, even when
            // the WAL would otherwise accumulate silently in the sidecar file.
            using var chk = cn.CreateCommand();
            chk.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
            chk.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqliteSink write failed for {Symbol}", tick.CanonicalSymbol);
            ErrorLog.Append($"SqliteSink write failed for {tick.CanonicalSymbol}: {ex}");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Returns the cached connection for <paramref name="symbol"/>, opening and
    /// initialising it on first call (thread-safe via double-checked lock).
    /// </summary>
    private SQLiteConnection GetOrOpenConnection(string symbol)
    {
        if (_connections.TryGetValue(symbol, out var existing))
            return existing;

        lock (_initLock)
        {
            if (_connections.TryGetValue(symbol, out existing))
                return existing;

            var file = Path.Combine(_directory, symbol + ".sqlite");
            if (!File.Exists(file)) SQLiteConnection.CreateFile(file);

            var cn = new SQLiteConnection($"Data Source={file};Version=3;");
            cn.Open();
            ApplyPragmas(cn);

            using (var cmd = cn.CreateCommand())
            {
                cmd.CommandText = "CREATE TABLE IF NOT EXISTS PriceData (TIMESTAMP real, CLOSE real)";
                cmd.ExecuteNonQuery();
            }

            _connections[symbol] = cn;
            _logger.LogDebug("SqliteSink opened connection for {Symbol} (WAL mode)", symbol);
            return cn;
        }
    }

    /// <summary>
    /// Sets the recommended PRAGMAs on a freshly-opened connection.
    /// WAL is persisted in the file header; SYNCHRONOUS and cache_size are connection-level.
    /// </summary>
    private static void ApplyPragmas(SQLiteConnection cn)
    {
        foreach (var sql in new[]
        {
            "PRAGMA journal_mode = WAL",       // persisted – much faster concurrent writes
            "PRAGMA synchronous  = NORMAL",    // per-connection – safe & fast (no full fsync per write)
            "PRAGMA cache_size   = -4000",     // 4 MB page cache
            "PRAGMA temp_store   = MEMORY",    // temp tables/indexes in RAM
        })
        {
            using var cmd = new SQLiteCommand(sql, cn);
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        foreach (var cn in _connections.Values)
        {
            try { cn.Dispose(); } catch { /* ignore shutdown errors */ }
        }
        _connections.Clear();
    }
}
