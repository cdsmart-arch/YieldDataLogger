using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Controllers;
using YieldDataLogger.Api.Data;

namespace YieldDataLogger.Api.Storage.Sql;

/// <summary>EF Core implementation of IPriceHistoryReader over the YieldDbContext.</summary>
public sealed class SqlPriceHistoryReader : IPriceHistoryReader
{
    private readonly IDbContextFactory<YieldDbContext> _factory;

    public SqlPriceHistoryReader(IDbContextFactory<YieldDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<PriceTickDto>> GetHistoryAsync(
        string symbol, double? fromTs, double? toTs, int take, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.PriceTicks.AsNoTracking().Where(p => p.Symbol == symbol);
        if (fromTs is double f) q = q.Where(p => p.TsUnix >= f);
        if (toTs is double t) q = q.Where(p => p.TsUnix <= t);
        q = q.OrderByDescending(p => p.TsUnix).Take(take);
        return await q.Select(p => new PriceTickDto(p.Symbol, p.TsUnix, p.Price, p.Source)).ToListAsync(ct);
    }

    public async Task<PriceTickDto?> GetLatestAsync(string symbol, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PriceTicks.AsNoTracking()
            .Where(p => p.Symbol == symbol)
            .OrderByDescending(p => p.TsUnix)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : new PriceTickDto(row.Symbol, row.TsUnix, row.Price, row.Source);
    }
}
