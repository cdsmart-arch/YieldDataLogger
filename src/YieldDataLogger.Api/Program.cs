using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data;
using YieldDataLogger.Api.Hosting;
using YieldDataLogger.Api.Sinks;
using YieldDataLogger.Collector.DependencyInjection;
using YieldDataLogger.Core.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// --- Web stack ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- Data layer (Azure SQL in prod, LocalDB in dev) ---
var connectionString = builder.Configuration.GetConnectionString("YieldDb")
    ?? throw new InvalidOperationException("ConnectionStrings:YieldDb is required");
builder.Services.AddDbContextFactory<YieldDbContext>(opts => opts.UseSqlServer(connectionString));

// --- Collector pipeline (catalog, sources, hosted services, local sinks) ---
// Local Sqlite/Scid sinks are disabled by default in this project's appsettings so only
// SqlPriceSink is active; SignalRPriceSink lands in Phase 3b.
builder.Services.AddYieldCollector(builder.Configuration);
builder.Services.AddSingleton<IPriceSink, SqlPriceSink>();

// Runs migrations + seeds the Instruments mirror once the catalog is loaded.
builder.Services.AddHostedService<DatabaseInitializer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();
