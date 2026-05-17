using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace YieldDataLogger.Api.Storage.Sqlite;

/// <summary>
/// Ensures the SQLite file + schema + WAL mode are ready before the first write. Registered
/// as IHostedService so it runs once at startup, before the collector sources start producing.
/// Mirrors <see cref="Tables.TablesInitializer"/> for the Tables backend.
///
/// Schema: a single Ticks table keyed on (Symbol, TsMicros). TsMicros is a long because we
/// want integer-equality dedup; the API surface still speaks fractional unix seconds.
/// </summary>
public sealed class SqliteInitializer : IHostedService
{
    private readonly IOptions<StorageOptions> _options;
    private readonly ILogger<SqliteInitializer> _logger;

    public SqliteInitializer(IOptions<StorageOptions> options, ILogger<SqliteInitializer> logger)
    {
        _options = options;
        _logger  = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = _options.Value.Sqlite.Path;
        var dir  = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var cn = new SqliteConnection(BuildConnectionString(path));
        cn.Open();

        // WAL is persisted in the file header. Set it once on first open and every subsequent
        // connection (writer or reader) inherits it. synchronous=NORMAL trades a full fsync
        // per commit for a small data-loss window on power failure, which is acceptable for
        // tick data the Agent backfills anyway.
        Exec(cn, "PRAGMA journal_mode=WAL;");
        Exec(cn, "PRAGMA synchronous=NORMAL;");
        Exec(cn, "PRAGMA temp_store=MEMORY;");

        Exec(cn, """
            CREATE TABLE IF NOT EXISTS Ticks (
                Symbol   TEXT    NOT NULL,
                TsMicros INTEGER NOT NULL,
                Price    REAL    NOT NULL,
                Source   TEXT    NOT NULL,
                PRIMARY KEY (Symbol, TsMicros)
            ) WITHOUT ROWID;
            """);

        _logger.LogInformation("SQLite storage ready: {Path}", Path.GetFullPath(path));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string BuildConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

    private static void Exec(SqliteConnection cn, string sql)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
