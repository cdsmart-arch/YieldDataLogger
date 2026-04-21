using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Api.Data;
using YieldDataLogger.Api.Data.Entities;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Sinks;

/// <summary>
/// Writes ticks to the shared SQL Server / Azure SQL database using EF Core. Uses the
/// pooled DbContext factory so each write is a short-lived scope (the dispatcher fans
/// out from every hosted service, so we can't rely on a per-request scope here).
///
/// Dedup mirrors the SqliteSink: consecutive identical prices for the same symbol are
/// dropped, which both saves write load and avoids primary-key collisions when ticks
/// arrive with the same TsUnix.
/// </summary>
public sealed class SqlPriceSink : IPriceSink
{
    private readonly IDbContextFactory<YieldDbContext> _factory;
    private readonly ILogger<SqlPriceSink> _logger;
    private readonly ConcurrentDictionary<string, double> _lastPriceBySymbol = new(StringComparer.Ordinal);

    public string Name => "sql";

    public SqlPriceSink(IDbContextFactory<YieldDbContext> factory, ILogger<SqlPriceSink> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        if (_lastPriceBySymbol.TryGetValue(tick.CanonicalSymbol, out var last) && last == tick.Price)
            return;
        _lastPriceBySymbol[tick.CanonicalSymbol] = tick.Price;

        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.PriceTicks.Add(new PriceTickEntity
            {
                Symbol = tick.CanonicalSymbol,
                TsUnix = tick.UnixTimeSeconds,
                Price = tick.Price,
                Source = tick.Source,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException dup) when (IsDuplicateKey(dup))
        {
            // Two sources racing the same (symbol, ts) -> harmless; the first writer wins.
            _logger.LogDebug("SqlPriceSink dup {Symbol} @ {Ts}", tick.CanonicalSymbol, tick.UnixTimeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqlPriceSink write failed for {Symbol}", tick.CanonicalSymbol);
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        // SQL Server duplicate-key errors surface as 2601/2627 inside the inner SqlException.
        var inner = ex.InnerException?.Message ?? "";
        return inner.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || inner.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
    }
}
