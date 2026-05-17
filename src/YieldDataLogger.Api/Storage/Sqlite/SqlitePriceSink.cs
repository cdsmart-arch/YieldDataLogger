using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Storage.Sqlite;

/// <summary>
/// Writes every tick to the single-file SQLite store on the API host (Hetzner). Mirrors the
/// dedup-by-PK pattern of <see cref="Tables.TablePriceSink"/>: INSERT OR IGNORE silently
/// skips duplicates so the dispatcher can be relaxed about retries and source races.
///
/// One process-wide writer is fine here because the only writer is the in-process Collector
/// pipeline. Microsoft.Data.Sqlite uses connection pooling under the hood, so we just open
/// and dispose a SqliteConnection per call — no manual cache needed and no thread-affinity
/// gotchas.
/// </summary>
public sealed class SqlitePriceSink : IPriceSink
{
    private readonly string _connectionString;
    private readonly ILogger<SqlitePriceSink> _logger;

    public string Name => "sqlite";

    public SqlitePriceSink(IOptions<StorageOptions> options, ILogger<SqlitePriceSink> logger)
    {
        _connectionString = SqliteInitializer.BuildConnectionString(options.Value.Sqlite.Path);
        _logger = logger;
    }

    public ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        try
        {
            using var cn = new SqliteConnection(_connectionString);
            cn.Open();
            using var cmd = cn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO Ticks (Symbol, TsMicros, Price, Source) VALUES ($s, $t, $p, $src)";
            cmd.Parameters.AddWithValue("$s", tick.CanonicalSymbol);
            cmd.Parameters.AddWithValue("$t", (long)(tick.UnixTimeSeconds * 1_000_000d));
            cmd.Parameters.AddWithValue("$p", tick.Price);
            cmd.Parameters.AddWithValue("$src", tick.Source ?? "");
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqlitePriceSink write failed for {Symbol}", tick.CanonicalSymbol);
        }
        return ValueTask.CompletedTask;
    }
}
