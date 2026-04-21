using Azure;
using Azure.Data.Tables;

namespace YieldDataLogger.Api.Storage.Tables;

/// <summary>
/// Azure Table Storage entity for a single price tick.
///   PartitionKey = canonical symbol (DAX, BUND, etc.) -> all ticks for one symbol co-located.
///   RowKey       = inverted microsecond timestamp (see TableRowKey) so newest sorts first.
///   TsUnix       = actual fractional-seconds timestamp (faster to read than decoding RowKey).
/// </summary>
public sealed class PriceTickTableEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public double Price { get; set; }
    public string Source { get; set; } = default!;
    public double TsUnix { get; set; }

    public static PriceTickTableEntity From(string symbol, double tsUnix, double price, string source) => new()
    {
        PartitionKey = symbol,
        RowKey = TableRowKey.FromUnixSeconds(tsUnix),
        Price = price,
        Source = source,
        TsUnix = tsUnix,
    };
}
