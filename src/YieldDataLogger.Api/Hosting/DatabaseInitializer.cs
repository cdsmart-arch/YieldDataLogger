using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data;

namespace YieldDataLogger.Api.Hosting;

/// <summary>
/// Applies EF migrations on startup so the PriceTicks table exists before the first write.
/// Only registered when the SQL backend is enabled (Storage:Sql:Enabled = true).
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
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            _logger.LogInformation("Applying EF migrations...");
            await db.Database.MigrateAsync(cancellationToken);
        }
        else if (!(await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
        {
            _logger.LogWarning("No EF migrations present; falling back to EnsureCreated for LocalDB.");
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
