using Microsoft.AspNetCore.Mvc;
using YieldDataLogger.Api.Storage;

namespace YieldDataLogger.Api.Controllers;

[ApiController]
[Route("api/ticks")]
public sealed class TicksController : ControllerBase
{
    private readonly IPriceHistoryReader _reader;

    public TicksController(IPriceHistoryReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Returns raw ticks for <paramref name="symbol"/>, newest first. Pass fromTs/toTs
    /// (unix seconds) to bound the window. NT8 and Sierra agents use this to backfill
    /// local stores before subscribing to the live SignalR feed (Phase 3b).
    /// </summary>
    [HttpGet("{symbol}")]
    public async Task<ActionResult<IEnumerable<PriceTickDto>>> GetHistory(
        string symbol,
        [FromQuery] double? fromTs,
        [FromQuery] double? toTs,
        [FromQuery] int take = 1000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return BadRequest("symbol is required");
        if (take <= 0 || take > 100_000) return BadRequest("take must be between 1 and 100000");

        var rows = await _reader.GetHistoryAsync(symbol, fromTs, toTs, take, ct);
        return Ok(rows);
    }

    [HttpGet("{symbol}/latest")]
    public async Task<ActionResult<PriceTickDto>> GetLatest(string symbol, CancellationToken ct = default)
    {
        var row = await _reader.GetLatestAsync(symbol, ct);
        return row is null ? NotFound() : Ok(row);
    }
}

public sealed record PriceTickDto(string Symbol, double TsUnix, double Price, string Source);
