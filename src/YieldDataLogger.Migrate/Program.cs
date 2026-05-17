using System.Diagnostics;
using Azure.Data.Tables;
using Microsoft.Data.Sqlite;
using YieldDataLogger.Api.Storage.Sqlite;
using YieldDataLogger.Api.Storage.Tables;

// One-shot migration: stream every row out of the Azure Tables PriceTicks table and insert
// into the new on-disk SQLite database. Idempotent via INSERT OR IGNORE on (Symbol, TsMicros),
// so re-running just tops up new rows and skips ones already present. Safe to run while the
// Hetzner Api container is also writing — both target the same primary key.
//
// Usage:
//   dotnet run --project src/YieldDataLogger.Migrate -- \
//     --connection "<azure-storage-connection-string>" \
//     --sqlite     "/var/lib/ydl/ydl.sqlite" \
//     [--table     PriceTicks] \
//     [--batch     1000]

var connection = GetArg(args, "--connection") ?? Environment.GetEnvironmentVariable("AZURE_TABLES_CONNECTION_STRING");
var sqlitePath = GetArg(args, "--sqlite")     ?? Environment.GetEnvironmentVariable("YDL_SQLITE_PATH");
var tableName  = GetArg(args, "--table")      ?? "PriceTicks";
var batchSize  = int.TryParse(GetArg(args, "--batch"), out var b) ? b : 1000;

if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(sqlitePath))
{
    Console.Error.WriteLine("usage: --connection <azure-conn> --sqlite <path> [--table PriceTicks] [--batch 1000]");
    return 2;
}

Console.WriteLine($"Source: Azure Table '{tableName}'");
Console.WriteLine($"Target: {Path.GetFullPath(sqlitePath)}");
Console.WriteLine();

// Ensure schema + WAL on the target file.
EnsureSchema(sqlitePath);

var service = new TableServiceClient(connection);
var table   = service.GetTableClient(tableName);

var stopwatch  = Stopwatch.StartNew();
var perSymbol  = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
long totalRead = 0, totalWritten = 0;

await using var cn = new SqliteConnection(SqliteInitializer.BuildConnectionString(sqlitePath));
await cn.OpenAsync();

await using var tx  = (SqliteTransaction)await cn.BeginTransactionAsync();
await using var cmd = cn.CreateCommand();
cmd.Transaction = tx;
cmd.CommandText = "INSERT OR IGNORE INTO Ticks (Symbol, TsMicros, Price, Source) VALUES ($s, $t, $p, $src)";
var pS   = cmd.Parameters.Add("$s",   SqliteType.Text);
var pT   = cmd.Parameters.Add("$t",   SqliteType.Integer);
var pP   = cmd.Parameters.Add("$p",   SqliteType.Real);
var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);

var batchCount = 0;
SqliteTransaction current = tx;

await foreach (var entity in table.QueryAsync<PriceTickTableEntity>(maxPerPage: 1000))
{
    totalRead++;
    pS.Value   = entity.PartitionKey;
    pT.Value   = (long)(entity.TsUnix * 1_000_000d);
    pP.Value   = entity.Price;
    pSrc.Value = entity.Source ?? "";

    var rows = cmd.ExecuteNonQuery();
    totalWritten += rows;
    perSymbol.TryGetValue(entity.PartitionKey, out var prev);
    perSymbol[entity.PartitionKey] = prev + rows;

    batchCount++;
    if (batchCount >= batchSize)
    {
        await current.CommitAsync();
        await current.DisposeAsync();
        current = (SqliteTransaction)await cn.BeginTransactionAsync();
        cmd.Transaction = current;
        batchCount = 0;
        Console.WriteLine($"  ... read={totalRead:N0} written={totalWritten:N0} ({stopwatch.Elapsed.TotalSeconds:F1}s)");
    }
}

await current.CommitAsync();
await current.DisposeAsync();
stopwatch.Stop();

Console.WriteLine();
Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds:F1}s.");
Console.WriteLine($"Read     : {totalRead:N0}");
Console.WriteLine($"Written  : {totalWritten:N0}   (skipped duplicates: {totalRead - totalWritten:N0})");
Console.WriteLine();
Console.WriteLine("Rows per symbol:");
foreach (var (sym, count) in perSymbol.OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {sym,-12} {count,12:N0}");

return 0;

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static void EnsureSchema(string path)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

    using var cn = new SqliteConnection(SqliteInitializer.BuildConnectionString(path));
    cn.Open();
    Exec(cn, "PRAGMA journal_mode=WAL;");
    Exec(cn, "PRAGMA synchronous=NORMAL;");
    Exec(cn, """
        CREATE TABLE IF NOT EXISTS Ticks (
            Symbol   TEXT    NOT NULL,
            TsMicros INTEGER NOT NULL,
            Price    REAL    NOT NULL,
            Source   TEXT    NOT NULL,
            PRIMARY KEY (Symbol, TsMicros)
        ) WITHOUT ROWID;
        """);
}

static void Exec(SqliteConnection cn, string sql)
{
    using var c = cn.CreateCommand();
    c.CommandText = sql;
    c.ExecuteNonQuery();
}
