using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using YieldDataLogger.Api.Controllers;

namespace YieldDataLogger.Api.Storage.Sqlite;

/// <summary>
/// Read-side for /api/ticks/{symbol} when Storage.Backend = "sqlite". Newest-first ordering
/// matches the TablePriceHistoryReader contract so the Agent's HistoryBackfillService keeps
/// working unchanged.
/// </summary>
public sealed class SqlitePriceHistoryReader : IPriceHistoryReader
{
    private readonly string _connectionString;

    public SqlitePriceHistoryReader(IOptions<StorageOptions> options)
    {
        _connectionString = SqliteInitializer.BuildConnectionString(options.Value.Sqlite.Path);
    }

    public async Task<IReadOnlyList<PriceTickDto>> GetHistoryAsync(
        string symbol, double? fromTs, double? toTs, int take, CancellationToken ct)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);

        await using var cmd = cn.CreateCommand();
        var sql = new System.Text.StringBuilder("SELECT Symbol, TsMicros, Price, Source FROM Ticks WHERE Symbol = $s");
        cmd.Parameters.AddWithValue("$s", symbol);
        if (fromTs is double f)
        {
            sql.Append(" AND TsMicros >= $from");
            cmd.Parameters.AddWithValue("$from", (long)(f * 1_000_000d));
        }
        if (toTs is double t)
        {
            sql.Append(" AND TsMicros <= $to");
            cmd.Parameters.AddWithValue("$to", (long)(t * 1_000_000d));
        }
        sql.Append(" ORDER BY TsMicros DESC LIMIT $take");
        cmd.Parameters.AddWithValue("$take", take);
        cmd.CommandText = sql.ToString();

        var results = new List<PriceTickDto>(Math.Min(take, 1024));
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var sym = rdr.GetString(0);
            var ts  = rdr.GetInt64(1) / 1_000_000d;
            var px  = rdr.GetDouble(2);
            var src = rdr.GetString(3);
            results.Add(new PriceTickDto(sym, ts, px, src));
        }
        return results;
    }

    public async Task<PriceTickDto?> GetLatestAsync(string symbol, CancellationToken ct)
    {
        await using var cn = new SqliteConnection(_connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT Symbol, TsMicros, Price, Source FROM Ticks WHERE Symbol = $s ORDER BY TsMicros DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$s", symbol);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (!await rdr.ReadAsync(ct)) return null;
        return new PriceTickDto(rdr.GetString(0), rdr.GetInt64(1) / 1_000_000d, rdr.GetDouble(2), rdr.GetString(3));
    }
}
