using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YieldDataLogger.Api.Data;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Controllers;

[ApiController]
[Route("api/instruments")]
public sealed class InstrumentsController : ControllerBase
{
    private readonly IDbContextFactory<YieldDbContext> _factory;
    private readonly InstrumentCatalog _catalog;

    public InstrumentsController(IDbContextFactory<YieldDbContext> factory, InstrumentCatalog catalog)
    {
        _factory = factory;
        _catalog = catalog;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<InstrumentDto>>> GetAll(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Instruments.AsNoTracking()
            .OrderBy(i => i.Category).ThenBy(i => i.CanonicalSymbol)
            .Select(i => new InstrumentDto(i.CanonicalSymbol, i.InvestingPid, i.CnbcSymbol, i.Category))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{symbol}")]
    public async Task<ActionResult<InstrumentDto>> Get(string symbol, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Instruments.AsNoTracking()
            .FirstOrDefaultAsync(i => i.CanonicalSymbol == symbol, ct);
        if (row is null) return NotFound();
        return Ok(new InstrumentDto(row.CanonicalSymbol, row.InvestingPid, row.CnbcSymbol, row.Category));
    }

    /// <summary>
    /// Upserts an instrument in both the JSON catalog (source-of-truth for the running collector)
    /// and the DB mirror. Phase 3c will lock this behind an admin role.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<InstrumentDto>> Upsert([FromBody] InstrumentDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.CanonicalSymbol))
            return BadRequest("canonicalSymbol required");
        if (dto.InvestingPid is null && string.IsNullOrWhiteSpace(dto.CnbcSymbol))
            return BadRequest("at least one of investingPid or cnbcSymbol required");

        var instr = new Instrument
        {
            CanonicalSymbol = dto.CanonicalSymbol,
            InvestingPid = dto.InvestingPid,
            CnbcSymbol = string.IsNullOrWhiteSpace(dto.CnbcSymbol) ? null : dto.CnbcSymbol,
            Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category,
        };

        try { _catalog.Upsert(instr); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
        _catalog.Save();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Instruments.FirstOrDefaultAsync(i => i.CanonicalSymbol == dto.CanonicalSymbol, ct);
        if (existing is null)
        {
            db.Instruments.Add(new Data.Entities.InstrumentEntity
            {
                CanonicalSymbol = instr.CanonicalSymbol,
                InvestingPid = instr.InvestingPid,
                CnbcSymbol = instr.CnbcSymbol,
                Category = instr.Category,
            });
        }
        else
        {
            existing.InvestingPid = instr.InvestingPid;
            existing.CnbcSymbol = instr.CnbcSymbol;
            existing.Category = instr.Category;
        }
        await db.SaveChangesAsync(ct);

        return Ok(dto);
    }

    [HttpDelete("{symbol}")]
    public async Task<IActionResult> Delete(string symbol, CancellationToken ct = default)
    {
        if (!_catalog.Remove(symbol)) return NotFound();
        _catalog.Save();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Instruments.FirstOrDefaultAsync(i => i.CanonicalSymbol == symbol, ct);
        if (row is not null)
        {
            db.Instruments.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}

public sealed record InstrumentDto(string CanonicalSymbol, int? InvestingPid, string? CnbcSymbol, string? Category);
