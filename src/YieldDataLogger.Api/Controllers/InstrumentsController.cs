using Microsoft.AspNetCore.Mvc;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Controllers;

/// <summary>
/// Exposes the instrument catalog. Source of truth is instruments.json on disk - loaded
/// into <see cref="InstrumentCatalog"/> at startup by the collector - so this controller
/// just proxies the in-memory catalog and round-trips admin edits back to the JSON file.
/// Keeping the catalog out of storage avoids drift between the API, the collector and the
/// Phase 4 offline agent (which reads the same JSON).
/// </summary>
[ApiController]
[Route("api/instruments")]
public sealed class InstrumentsController : ControllerBase
{
    private readonly InstrumentCatalog _catalog;

    public InstrumentsController(InstrumentCatalog catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    public ActionResult<IEnumerable<InstrumentDto>> GetAll()
    {
        var rows = _catalog.All
            .OrderBy(i => i.Category ?? "").ThenBy(i => i.CanonicalSymbol, StringComparer.OrdinalIgnoreCase)
            .Select(i => new InstrumentDto(i.CanonicalSymbol, i.InvestingPid, i.CnbcSymbol, i.Category))
            .ToArray();
        return Ok(rows);
    }

    [HttpGet("{symbol}")]
    public ActionResult<InstrumentDto> Get(string symbol)
    {
        if (!_catalog.BySymbol.TryGetValue(symbol, out var instr)) return NotFound();
        return Ok(new InstrumentDto(instr.CanonicalSymbol, instr.InvestingPid, instr.CnbcSymbol, instr.Category));
    }

    [HttpPost]
    public ActionResult<InstrumentDto> Upsert([FromBody] InstrumentDto dto)
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

        return Ok(dto);
    }

    [HttpDelete("{symbol}")]
    public IActionResult Delete(string symbol)
    {
        if (!_catalog.Remove(symbol)) return NotFound();
        _catalog.Save();
        return NoContent();
    }
}

public sealed record InstrumentDto(string CanonicalSymbol, int? InvestingPid, string? CnbcSymbol, string? Category);
