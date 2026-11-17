using System.Collections.Concurrent;
using System.Data.SQLite;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Models;
using YieldDataLogger.Core.Sierra;

namespace YieldDataLogger.Core.Sinks;

/// <summary>
/// Writes ticks to Sierra Chart intraday (.scid) files - one <c>_SYMBOL.scid</c> per symbol in
/// the configured Sierra Data folder. Only symbols in <see cref="AllowedSymbols"/> are written;
/// both the folder and the symbol set can be changed at runtime via <see cref="Configure"/>
/// (the Agent wires that to the Manager-edited subscriptions.json).
///
/// File layout is Sierra's documented format: 56-byte header + 40-byte s_IntradayRecord
/// records, DateTime = int64 microseconds since 1899-12-30 UTC, strictly ascending.
///
/// History: when a history directory (the SqliteSink folder) is supplied, a new or empty
/// .scid file is seeded from the symbol's local SQLite history, and
/// <see cref="MergeFromHistory"/> lets the backfill service splice a filled gap back into
/// the file in timestamp order. SQLite is the source of truth; the .scid is derived from it.
/// Deleting a .scid file (with Sierra closed) makes the sink rebuild it on the next tick.
///
/// Sharing / permissions: every open uses FileShare.ReadWrite|Delete so Sierra Chart can
/// keep the file open while we append. Files created here (usually by the Agent service
/// running as LocalSystem) get an explicit BUILTIN\Users Modify ACE so the interactive
/// user's Sierra process - and the user themselves - can read, rewrite or delete them.
/// </summary>
public sealed class ScidSink : IPriceSink
{
    private const int HeaderSize = ScidFormat.HeaderSize;
    private const int RecordSize = ScidFormat.RecordSize;

    private readonly ILogger<ScidSink> _logger;
    private readonly string? _historyDirectory;
    private readonly object _configGate = new();
    private readonly ConcurrentDictionary<string, SymbolState> _states = new(StringComparer.Ordinal);

    private volatile string _folder;
    private volatile HashSet<string> _allowed;

    public string Name => "scid";

    /// <summary>Folder the .scid files are written to.</summary>
    public string Folder => _folder;

    /// <summary>Symbols this sink currently writes. Empty set = write nothing.</summary>
    public IReadOnlyCollection<string> AllowedSymbols => _allowed;

    public ScidSink(
        string sierraDataFolder,
        IEnumerable<string> allowedSymbols,
        ILogger<ScidSink> logger,
        string? historyDirectory = null)
    {
        _logger = logger;
        _historyDirectory = string.IsNullOrWhiteSpace(historyDirectory) ? null : historyDirectory;
        _folder  = sierraDataFolder ?? throw new ArgumentNullException(nameof(sierraDataFolder));
        _allowed = new HashSet<string>(StringComparer.Ordinal);
        Configure(sierraDataFolder, allowedSymbols);
    }

    public bool IsEnabledFor(string symbol) => _allowed.Contains(symbol);

    /// <summary>
    /// Replaces the target folder and symbol set. Newly enabled symbols whose .scid file is
    /// missing or empty are seeded from local history in the background, so a symbol that
    /// rarely ticks still shows up in Sierra straight away.
    /// </summary>
    public void Configure(string folder, IEnumerable<string> symbols)
    {
        var next = new HashSet<string>(
            symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()),
            StringComparer.Ordinal);

        string[] toPrepare;
        lock (_configGate)
        {
            var folderChanged = !string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase);
            toPrepare = folderChanged ? next.ToArray() : next.Except(_allowed).ToArray();
            if (folderChanged) _states.Clear();
            _folder  = folder;
            _allowed = next;
        }

        _logger.LogInformation("ScidSink -> {Folder}: {Symbols}", folder,
            next.Count == 0 ? "(none)" : string.Join(",", next.OrderBy(s => s)));

        if (toPrepare.Length == 0) return;
        _ = Task.Run(() =>
        {
            foreach (var symbol in toPrepare)
            {
                if (!IsEnabledFor(symbol)) continue;
                try
                {
                    var state = GetState(symbol);
                    lock (state.Gate) EnsureInitialized(symbol, state);
                }
                catch (Exception ex) { LogFailure(symbol, "prepare", ex); }
            }
        });
    }

    public ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        var symbol = tick.CanonicalSymbol;
        if (!_allowed.Contains(symbol))
            return ValueTask.CompletedTask;

        try
        {
            var state = GetState(symbol);
            lock (state.Gate)
            {
                EnsureInitialized(symbol, state);

                var rawUs = ToSierraUs(tick.UnixTimeSeconds);
                // Already in the file (e.g. a seed/merge from SQLite just included this tick),
                // or out of order - Sierra requires ascending times, so drop it.
                if (rawUs <= state.LastRawUs) return ValueTask.CompletedTask;

                var us = Math.Max(rawUs, state.LastWrittenUs + 1);
                using var fs = OpenShared(state.File, FileMode.Open);
                fs.Seek(0, SeekOrigin.End);
                using var w = new BinaryWriter(fs);
                WriteRecord(w, us, tick.Price);

                state.LastRawUs     = rawUs;
                state.LastWrittenUs = us;
            }
        }
        catch (Exception ex)
        {
            LogFailure(symbol, "write", ex);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Re-derives the tail of a symbol's .scid file from SQLite, starting at
    /// <paramref name="fromUnixSeconds"/>. Called after a history backfill inserted rows so
    /// a filled gap lands in the file in timestamp order (live ticks that arrived after the
    /// gap are rewritten behind it). Sierra Chart may need "Reload and Recalculate" to show
    /// records spliced into a chart that is already open.
    /// </summary>
    public void MergeFromHistory(string symbol, double fromUnixSeconds)
    {
        if (!_allowed.Contains(symbol) || _historyDirectory is null) return;
        try
        {
            var state = GetState(symbol);
            lock (state.Gate)
            {
                EnsureInitialized(symbol, state);
                MergeLocked(symbol, state, ToSierraUs(fromUnixSeconds));
            }
        }
        catch (Exception ex)
        {
            LogFailure(symbol, "merge", ex);
        }
    }

    // -------------------------------------------------------------------------

    private SymbolState GetState(string symbol)
    {
        var folder = _folder;
        return _states.GetOrAdd(symbol, s => new SymbolState(Path.Combine(folder, $"_{s}.scid")));
    }

    /// <summary>
    /// Makes sure the file exists with a valid header and whole records, and loads the last
    /// timestamp. A missing or record-less file is seeded from SQLite history.
    /// Caller holds <c>state.Gate</c>.
    /// </summary>
    private void EnsureInitialized(string symbol, SymbolState state)
    {
        // A file deleted from under us (user wants a rebuild) re-initialises from scratch.
        if (state.Initialized && File.Exists(state.File)) return;

        state.Initialized = false;
        state.LastRawUs = state.LastWrittenUs = long.MinValue;

        var dir = Path.GetDirectoryName(state.File);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var created = false;
        long records;
        using (var fs = OpenShared(state.File, FileMode.OpenOrCreate))
        {
            if (fs.Length < HeaderSize)
            {
                fs.SetLength(0);
                using var w = new BinaryWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true);
                WriteHeader(w);
                created = true;
            }

            // Drop a torn trailing record or zero-filled records (power loss mid-write);
            // otherwise appends would be misaligned, or resume from a 1899 timestamp.
            records = ScidFormat.TrimDamagedTail(fs, out var torn, out var bad);
            if (torn > 0 || bad > 0)
                _logger.LogWarning("ScidSink trimmed a damaged tail from {File}: {Bytes} torn bytes, {Records} records with no valid time.",
                    state.File, torn, bad);

            if (records > 0)
            {
                var last = ReadTimestamp(fs, records - 1);
                state.LastRawUs = state.LastWrittenUs = last;
            }
        }

        if (created) GrantUsersModify(state.File);
        state.Initialized = true;

        if (_historyDirectory is null) return;

        if (records == 0)
        {
            MergeLocked(symbol, state, long.MinValue);
            _logger.LogInformation("ScidSink seeded {File} from history ({Count} records).",
                state.File, CountRecords(state.File));
        }
        else
        {
            ReconcileLocked(symbol, state);
        }
    }

    /// <summary>
    /// An existing file (e.g. left by the legacy YieldLoggerUI app, or written while the
    /// Agent was offline) may be missing ticks that SQLite has. Compare record counts over
    /// SQLite's time range; if the file has fewer, re-derive that range from SQLite. Records
    /// older than SQLite's history are kept, so legacy data survives. Caller holds the gate.
    /// </summary>
    private void ReconcileLocked(string symbol, SymbolState state)
    {
        var sqliteFile = Path.Combine(_historyDirectory!, symbol + ".sqlite");
        if (!File.Exists(sqliteFile)) return;

        double minTs, maxTs;
        long sqliteCount;
        using (var cn = new SQLiteConnection($"Data Source={sqliteFile};Version=3;"))
        {
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), MIN(TIMESTAMP), MAX(TIMESTAMP) FROM PriceData";
            using var r = cmd.ExecuteReader();
            if (!r.Read() || r.IsDBNull(1)) return;
            sqliteCount = r.GetInt64(0);
            minTs = r.GetDouble(1);
            maxTs = r.GetDouble(2);
        }

        long fileCount;
        using (var fs = OpenShared(state.File, FileMode.Open))
        {
            var n = (fs.Length - HeaderSize) / RecordSize;
            fileCount = LowerBound(fs, n, ToSierraUs(maxTs) + 1) - LowerBound(fs, n, ToSierraUs(minTs));
        }

        if (fileCount >= sqliteCount) return;

        _logger.LogInformation(
            "ScidSink {File} has {FileCount} records in the history range vs {SqliteCount} in SQLite - filling from history.",
            state.File, fileCount, sqliteCount);
        MergeLocked(symbol, state, ToSierraUs(minTs));
    }

    /// <summary>
    /// Overwrites the file from the first record at/after <paramref name="fromUs"/> with the
    /// SQLite rows from that point on. Writes over the old tail then trims, so the file is
    /// never transiently empty. Caller holds <c>state.Gate</c>.
    /// </summary>
    private void MergeLocked(string symbol, SymbolState state, long fromUs)
    {
        var sqliteFile = Path.Combine(_historyDirectory!, symbol + ".sqlite");
        if (!File.Exists(sqliteFile)) return;

        var fromUnix = fromUs == long.MinValue ? double.MinValue : FromSierraUs(fromUs);

        using var fs = OpenShared(state.File, FileMode.Open);
        var count = (fs.Length - HeaderSize) / RecordSize;
        var idx   = LowerBound(fs, count, fromUs);
        long lastWritten = idx > 0 ? ReadTimestamp(fs, idx - 1) : long.MinValue;
        long lastRaw     = lastWritten;

        using var cn = new SQLiteConnection($"Data Source={sqliteFile};Version=3;");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT TIMESTAMP, CLOSE FROM PriceData WHERE TIMESTAMP >= @FROM ORDER BY TIMESTAMP";
        cmd.Parameters.AddWithValue("@FROM", fromUnix);
        using var reader = cmd.ExecuteReader();

        fs.Seek(HeaderSize + idx * RecordSize, SeekOrigin.Begin);
        using var buffered = new BufferedStream(fs, 1 << 20);
        using var w = new BinaryWriter(buffered);

        long written = 0;
        while (reader.Read())
        {
            var rawUs = ToSierraUs(reader.GetDouble(0));
            if (rawUs <= lastRaw) continue;
            var us = Math.Max(rawUs, lastWritten + 1);
            WriteRecord(w, us, reader.GetDouble(1));
            lastRaw = rawUs;
            lastWritten = us;
            written++;
        }

        // SQLite had nothing from this point on - keep the existing tail rather than
        // truncating live data we can't reproduce.
        if (written == 0) return;

        w.Flush();
        fs.SetLength(fs.Position);
        state.LastRawUs     = lastRaw;
        state.LastWrittenUs = lastWritten;
    }

    // File layout and sharing/ACL handling live in ScidFormat, shared with ScidTradeFile.
    private static long LowerBound(FileStream fs, long count, long us) => ScidFormat.LowerBound(fs, count, us);
    private static long ReadTimestamp(FileStream fs, long index) => ScidFormat.ReadTimestamp(fs, index);
    private static FileStream OpenShared(string file, FileMode mode) => ScidFormat.OpenShared(file, mode);
    private static void WriteHeader(BinaryWriter w) => ScidFormat.WriteHeader(w);
    private static void WriteRecord(BinaryWriter w, long sierraUs, double price) =>
        ScidFormat.WriteRecord(w, ScidRec.Price(sierraUs, price));
    private static long ToSierraUs(double unixSeconds) => ScidFormat.UnixSecondsToSierraUs(unixSeconds);
    private static double FromSierraUs(long us) => ScidFormat.SierraUsToUnixSeconds(us);
    private void GrantUsersModify(string file) => ScidFormat.GrantUsersModify(file, _logger);

    private static long CountRecords(string file)
    {
        try { return ScidFormat.RecordCount(new FileInfo(file).Length); }
        catch { return -1; }
    }

    private void LogFailure(string symbol, string op, Exception ex)
    {
        _logger.LogWarning(ex, "ScidSink {Op} failed for {Symbol}", op, symbol);
        ErrorLog.Append($"ScidSink {op} failed for {symbol}: {ex}");
    }

    private sealed class SymbolState
    {
        public SymbolState(string file) { File = file; }
        public string File { get; }
        public object Gate { get; } = new();
        public bool Initialized;
        public long LastRawUs = long.MinValue;
        public long LastWrittenUs = long.MinValue;
    }
}
