using System.Data.SQLite;
using System.IO;

namespace YieldDataLogger.Manager;

/// <summary>
/// Reads the per-symbol SQLite files the Agent's SqliteSink writes - the same binary path a
/// NinjaTrader indicator (phase 6) will consume - so the Manager dashboard verifies the
/// downstream shape end-to-end, not just the hub connection.
///
/// Opens each file read-only and with ShareDenyNone so we never block the Agent's writers.
/// </summary>
internal static class SqliteRowReader
{
    public sealed record SymbolData(
        string Symbol,
        long   Rows,
        double LastPrice,
        DateTime LastUpdateUtc,
        string? Error);

    public static IReadOnlyList<SymbolData> Read(string sinkDir, IEnumerable<string> symbols)
    {
        var results = new List<SymbolData>();
        if (string.IsNullOrWhiteSpace(sinkDir) || !Directory.Exists(sinkDir)) return results;

        foreach (var symbol in symbols)
        {
            var file = Path.Combine(sinkDir, symbol + ".sqlite");
            if (!File.Exists(file))
            {
                results.Add(new SymbolData(symbol, 0, 0, DateTime.MinValue, "no file yet"));
                continue;
            }

            try
            {
                var cs = $"Data Source={file};Version=3;Read Only=True;";
                using var c = new SQLiteConnection(cs);
                c.Open();

                using var q = new SQLiteCommand(
                    "SELECT COUNT(*), MAX(TIMESTAMP), (SELECT CLOSE FROM PriceData ORDER BY rowid DESC LIMIT 1) FROM PriceData",
                    c);
                using var r = q.ExecuteReader();
                if (!r.Read())
                {
                    results.Add(new SymbolData(symbol, 0, 0, DateTime.MinValue, "empty"));
                    continue;
                }

                var rows     = r.IsDBNull(0) ? 0L : r.GetInt64(0);
                var lastTs   = r.IsDBNull(1) ? 0d : r.GetDouble(1);
                var lastPx   = r.IsDBNull(2) ? 0d : r.GetDouble(2);
                var lastUtc  = lastTs > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)lastTs).UtcDateTime
                    : DateTime.MinValue;

                results.Add(new SymbolData(symbol, rows, lastPx, lastUtc, null));
            }
            catch (Exception ex)
            {
                results.Add(new SymbolData(symbol, 0, 0, DateTime.MinValue, ex.Message));
            }
        }

        return results;
    }
}
