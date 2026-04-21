using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Api.Storage.Tables;

/// <summary>
/// Writes every tick as a row in the configured Azure Table Storage table.
/// Dedup is handled centrally by the <see cref="Collector.Pipeline.TickDispatcher"/>, so this
/// sink only ever receives ticks whose price differs from the previous one for the same symbol.
/// A 409 Conflict is still possible if two sources race the same (symbol, microsecond) RowKey;
/// that's swallowed - first writer wins.
/// The TableClient is pre-created (CreateIfNotExists) by TablesInitializer before we run.
/// </summary>
public sealed class TablePriceSink : IPriceSink
{
    private readonly TableClient _client;
    private readonly ILogger<TablePriceSink> _logger;

    public string Name => "table";

    public TablePriceSink(TableClient client, ILogger<TablePriceSink> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        var entity = PriceTickTableEntity.From(tick.CanonicalSymbol, tick.UnixTimeSeconds, tick.Price, tick.Source);
        try
        {
            await _client.AddEntityAsync(entity, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException rfe) when (rfe.Status == 409)
        {
            _logger.LogDebug("TablePriceSink dup {Symbol} @ {Ts}", tick.CanonicalSymbol, tick.UnixTimeSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TablePriceSink write failed for {Symbol}", tick.CanonicalSymbol);
        }
    }
}
