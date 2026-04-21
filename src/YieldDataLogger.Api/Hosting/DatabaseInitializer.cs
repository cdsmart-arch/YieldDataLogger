using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data;
using YieldDataLogger.Api.Data.Entities;
using YieldDataLogger.Collector.Instruments;

namespace YieldDataLogger.Api.Hosting;

/// <summary>
/// Runs once at startup: applies pending EF migrations, then mirrors the JSON catalog into the
/// Instruments table so newly-added symbols show up via the REST API without a round-trip
/// through the admin endpoint. The InstrumentCatalog itself is loaded by the collector's
/// existing hosted service; we wait until after it's populated (StartAsync ordering is
/// deterministic because the collector loader was registered first in AddYieldCollector).
/// </summary>
public sealed class DatabaseInitializer : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(IServiceProvider sp, ILogger<DatabaseInitializer> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<YieldDbContext>>();
        var catalog = scope.ServiceProvider.GetRequiredService<InstrumentCatalog>();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Migrations over EnsureCreated so the CI-deployed Azure SQL gets real migration
        // history - EnsureCreated quietly stops tracking schema drift.
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            _logger.LogInformation("Applying EF migrations...");
            await db.Database.MigrateAsync(cancellationToken);
        }
        else if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            // No migrations generated yet (first-run dev). EnsureCreated so LocalDB has tables.
            _logger.LogWarning("No EF migrations present; falling back to EnsureCreated for LocalDB.");
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        // Mirror catalog JSON -> DB. Insert any missing rows; update anything that drifted.
        var dbRows = await db.Instruments.ToDictionaryAsync(i => i.CanonicalSymbol, StringComparer.OrdinalIgnoreCase, cancellationToken);
        int added = 0, updated = 0;
        foreach (var instr in catalog.All)
        {
            if (!dbRows.TryGetValue(instr.CanonicalSymbol, out var existing))
            {
                db.Instruments.Add(new InstrumentEntity
                {
                    CanonicalSymbol = instr.CanonicalSymbol,
                    InvestingPid = instr.InvestingPid,
                    CnbcSymbol = instr.CnbcSymbol,
                    Category = instr.Category,
                });
                added++;
            }
            else if (existing.InvestingPid != instr.InvestingPid ||
                     !string.Equals(existing.CnbcSymbol, instr.CnbcSymbol, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(existing.Category, instr.Category, StringComparison.OrdinalIgnoreCase))
            {
                existing.InvestingPid = instr.InvestingPid;
                existing.CnbcSymbol = instr.CnbcSymbol;
                existing.Category = instr.Category;
                updated++;
            }
        }
        if (added > 0 || updated > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded Instruments table: +{Added} ~{Updated}", added, updated);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
