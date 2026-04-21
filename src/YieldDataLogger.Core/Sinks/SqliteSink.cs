using System.Data.SQLite;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Core.Sinks;

/// <summary>
/// Writes each tick to a per-instrument SQLite file ({symbol}.sqlite) matching the
/// schema used by the original YieldLoggerUI app: PriceData(TIMESTAMP real, CLOSE real).
/// Kept compatible on purpose so the forthcoming NT8 indicator and any other legacy
/// reader can consume the same files without changes.
///
/// Dedup is handled centrally by the TickDispatcher, so this sink only ever sees ticks
/// whose price differs from the previous one for that symbol.
/// </summary>
public sealed class SqliteSink : IPriceSink
{
    private readonly string _directory;
    private readonly ILogger<SqliteSink> _logger;

    public string Name => "sqlite";

    public SqliteSink(string directory, ILogger<SqliteSink> logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    public ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        var file = Path.Combine(_directory, $"{tick.CanonicalSymbol}.sqlite");
        try
        {
            EnsureFile(file);
            EnsureTable(file);
            InsertRow(file, tick.UnixTimeSeconds, tick.Price);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqliteSink write failed for {Symbol}", tick.CanonicalSymbol);
            ErrorLog.Append($"SqliteSink write failed for {tick.CanonicalSymbol}: {ex}");
        }

        return ValueTask.CompletedTask;
    }

    private static void EnsureFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        if (!File.Exists(path))
            SQLiteConnection.CreateFile(path);
    }

    private static void EnsureTable(string path)
    {
        using var cn = new SQLiteConnection($"Data Source={path};Version=3;");
        cn.Open();
        using var cmd = new SQLiteCommand(
            "CREATE TABLE IF NOT EXISTS PriceData (TIMESTAMP real, CLOSE real)", cn);
        cmd.ExecuteNonQuery();
    }

    private static void InsertRow(string path, double timestamp, double price)
    {
        using var cn = new SQLiteConnection($"Data Source={path};Version=3;");
        cn.Open();
        using var cmd = new SQLiteCommand(
            "INSERT INTO PriceData (TIMESTAMP, CLOSE) VALUES (@TIMESTAMP, @CLOSE)", cn);
        cmd.Parameters.AddWithValue("@TIMESTAMP", timestamp);
        cmd.Parameters.AddWithValue("@CLOSE", price);
        cmd.ExecuteNonQuery();
    }
}
