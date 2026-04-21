using Azure.Data.Tables;
using YieldDataLogger.Api.Controllers;

namespace YieldDataLogger.Api.Storage.Tables;

/// <summary>
/// Reads ticks from Azure Table Storage. Because RowKey is the inverted-microsecond
/// encoding, a natural ascending scan over RowKey returns newest-first - we just take the
/// first N and we're done. History windows are translated into RowKey-range filters:
/// newer tsBound -> smaller RowKey, older tsBound -> bigger RowKey.
/// </summary>
public sealed class TablePriceHistoryReader : IPriceHistoryReader
{
    private readonly TableClient _client;

    public TablePriceHistoryReader(TableClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<PriceTickDto>> GetHistoryAsync(
        string symbol, double? fromTs, double? toTs, int take, CancellationToken ct)
    {
        // Build the RowKey range. Remember: newer tick -> smaller RowKey.
        //   "ticks newer than fromTs"  -> RowKey <= RowKeyOf(fromTs)
        //   "ticks older than toTs"    -> RowKey >= RowKeyOf(toTs)
        var filters = new List<string> { $"PartitionKey eq '{Escape(symbol)}'" };
        if (toTs is double t) filters.Add($"RowKey ge '{TableRowKey.FromUnixSeconds(t)}'");
        if (fromTs is double f) filters.Add($"RowKey le '{TableRowKey.FromUnixSeconds(f)}'");
        var filter = string.Join(" and ", filters);

        var results = new List<PriceTickDto>(Math.Min(take, 1024));
        await foreach (var row in _client.QueryAsync<PriceTickTableEntity>(filter, maxPerPage: Math.Min(take, 1000), cancellationToken: ct))
        {
            results.Add(new PriceTickDto(row.PartitionKey, row.TsUnix, row.Price, row.Source));
            if (results.Count >= take) break;
        }
        return results;
    }

    public async Task<PriceTickDto?> GetLatestAsync(string symbol, CancellationToken ct)
    {
        await foreach (var row in _client.QueryAsync<PriceTickTableEntity>(
            filter: $"PartitionKey eq '{Escape(symbol)}'",
            maxPerPage: 1,
            cancellationToken: ct))
        {
            return new PriceTickDto(row.PartitionKey, row.TsUnix, row.Price, row.Source);
        }
        return null;
    }

    // Table Storage OData filters use single quotes; escape any embedded quote in the symbol.
    private static string Escape(string value) => value.Replace("'", "''");
}
