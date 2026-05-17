namespace YieldDataLogger.Api.Storage;

/// <summary>
/// Binds the "Storage" section of appsettings.json. Either or both backends can run simultaneously
/// (useful for migration / A-B testing); Backend selects which one the REST API reads from.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// "table" (Azure Table Storage, original cloud backend), "sql" (Azure SQL / SQL Server),
    /// or "sqlite" (single-file local DB, used on the Hetzner deploy).
    /// </summary>
    public string Backend { get; set; } = "table";

    public TableStorageOptions Tables { get; set; } = new();
    public SqlStorageOptions Sql { get; set; } = new();
    public SqliteStorageOptions Sqlite { get; set; } = new();
}

public sealed class SqliteStorageOptions
{
    /// <summary>When true the SqlitePriceSink is wired into the dispatcher.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Path to the single-file SQLite database. The container mounts /data so the file
    /// survives container replacement; set this to "/data/ydl.sqlite" in prod via env var.
    /// </summary>
    public string Path { get; set; } = "ydl.sqlite";
}

public sealed class TableStorageOptions
{
    /// <summary>When true the TablePriceSink is wired into the dispatcher.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Azure Storage connection string. "UseDevelopmentStorage=true" targets Azurite
    /// on the default ports (local dev). In Azure use the full connection string or a
    /// managed-identity URL.
    /// </summary>
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";

    /// <summary>Name of the table holding price ticks. Must be alphanumeric, 3-63 chars.</summary>
    public string PriceTicksTableName { get; set; } = "PriceTicks";
}

public sealed class SqlStorageOptions
{
    /// <summary>When true the SqlPriceSink + EF DbContext + migration runner are wired.</summary>
    public bool Enabled { get; set; } = false;

    public string ConnectionString { get; set; } =
        "Server=(localdb)\\MSSQLLocalDB;Database=YieldDataLogger;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False";
}
