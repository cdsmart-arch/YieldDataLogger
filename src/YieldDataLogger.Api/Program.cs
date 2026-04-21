using Azure.Data.Tables;
using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data;
using YieldDataLogger.Api.Hosting;
using YieldDataLogger.Api.Sinks;
using YieldDataLogger.Api.Storage;
using YieldDataLogger.Api.Storage.Sql;
using YieldDataLogger.Api.Storage.Tables;
using YieldDataLogger.Collector.DependencyInjection;
using YieldDataLogger.Core.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// --- Web stack ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- Storage configuration ---
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
var backend = (storage.Backend ?? "table").Trim().ToLowerInvariant();

// --- Azure Table Storage (primary backend) ---
if (storage.Tables.Enabled || backend == "table")
{
    builder.Services.AddSingleton(sp => new TableServiceClient(storage.Tables.ConnectionString));
    builder.Services.AddSingleton(sp =>
    {
        var service = sp.GetRequiredService<TableServiceClient>();
        return service.GetTableClient(storage.Tables.PriceTicksTableName);
    });
    builder.Services.AddHostedService<TablesInitializer>();

    if (storage.Tables.Enabled)
        builder.Services.AddSingleton<IPriceSink, TablePriceSink>();
}

// --- Azure SQL / SQL Server (optional fallback backend) ---
if (storage.Sql.Enabled || backend == "sql")
{
    builder.Services.AddDbContextFactory<YieldDbContext>(opts => opts.UseSqlServer(storage.Sql.ConnectionString));
    builder.Services.AddHostedService<DatabaseInitializer>();

    if (storage.Sql.Enabled)
        builder.Services.AddSingleton<IPriceSink, SqlPriceSink>();
}

// --- History reader: one implementation based on Backend ---
switch (backend)
{
    case "sql":
        builder.Services.AddSingleton<IPriceHistoryReader, SqlPriceHistoryReader>();
        break;
    case "table":
    default:
        builder.Services.AddSingleton<IPriceHistoryReader, TablePriceHistoryReader>();
        break;
}

// --- Collector pipeline (catalog, sources, hosted services, local sinks) ---
// Local Sqlite/Scid sinks are off by default in this project's appsettings; only the
// cloud backend sinks registered above are active.
builder.Services.AddYieldCollector(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", backend, utc = DateTime.UtcNow }));

app.Run();
