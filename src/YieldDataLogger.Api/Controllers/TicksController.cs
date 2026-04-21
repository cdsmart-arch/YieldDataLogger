using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data;
using YieldDataLogger.Api.Data.Entities;

namespace YieldDataLogger.Api.Controllers;

[ApiController]
[Route("api/ticks")]
public sealed class TicksController : ControllerBase
{
    private readonly IDbContextFactory<YieldDbContext> _factory;

    public TicksController(IDbContextFactory<YieldDbContext> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Returns raw ticks for <paramref name="symbol"/>. The default is the most recent 1000
    /// ticks (descending by timestamp). Pass fromTs/toTs (unix seconds) to bound the window.
    /// NT8 and Sierra agents use this to backfill local stores before subscribing to the
    /// live SignalR feed.
    /// </summary>
    [HttpGet("{symbol}")]
    public async Task<ActionResult<IEnumerable<PriceTickDto>>> GetHistory(
        string symbol,
        [FromQuery] double? fromTs,
        [FromQuery] double? toTs,
        [FromQuery] int take = 1000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest("symbol is required");
        if (take <= 0 || take > 100_000)
            return BadRequest("take must be between 1 and 100000");

        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.PriceTicks.AsNoTracking().Where(p => p.Symbol == symbol);
        if (fromTs is double f) q = q.Where(p => p.TsUnix >= f);
        if (toTs is double t) q = q.Where(p => p.TsUnix <= t);
        q = q.OrderByDescending(p => p.TsUnix).Take(take);

        var rows = await q.Select(p => new PriceTickDto(p.Symbol, p.TsUnix, p.Price, p.Source)).ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{symbol}/latest")]
    public async Task<ActionResult<PriceTickDto>> GetLatest(string symbol, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PriceTicks.AsNoTracking()
            .Where(p => p.Symbol == symbol)
            .OrderByDescending(p => p.TsUnix)
            .FirstOrDefaultAsync(ct);
        if (row is null) return NotFound();
        return Ok(new PriceTickDto(row.Symbol, row.TsUnix, row.Price, row.Source));
    }
}

public sealed record PriceTickDto(string Symbol, double TsUnix, double Price, string Source);
