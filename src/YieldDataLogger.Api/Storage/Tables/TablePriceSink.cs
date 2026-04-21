using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Storage.Tables;

/// <summary>
/// Writes every tick as a row in the configured Azure Table Storage table.
/// - Dedup: drops consecutive identical prices per symbol (matches SqliteSink behaviour).
/// - Swallows 409 Conflict: harmless when two ticks resolve to the same microsecond RowKey.
/// The TableClient is pre-created (CreateIfNotExists) by TablesInitializer before we run.
/// </summary>
public sealed class TablePriceSink : IPriceSink
{
    private readonly TableClient _client;
    private readonly ILogger<TablePriceSink> _logger;
    private readonly ConcurrentDictionary<string, double> _lastPriceBySymbol = new(StringComparer.Ordinal);

    public string Name => "table";

    public TablePriceSink(TableClient client, ILogger<TablePriceSink> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        if (_lastPriceBySymbol.TryGetValue(tick.CanonicalSymbol, out var last) && last == tick.Price)
            return;
        _lastPriceBySymbol[tick.CanonicalSymbol] = tick.Price;

        var entity = PriceTickTableEntity.From(tick.CanonicalSymbol, tick.UnixTimeSeconds, tick.Price, tick.Source);
        try
        {
            await _client.AddEntityAsync(entity, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException rfe) when (rfe.Status == 409)
        {
            // Same (symbol, microsecond) already stored - first writer wins.
            _logger.LogDebug("TablePriceSink dup {Symbol} @ {Ts}", tick.CanonicalSymbol, tick.UnixTimeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TablePriceSink write failed for {Symbol}", tick.CanonicalSymbol);
        }
    }
}
