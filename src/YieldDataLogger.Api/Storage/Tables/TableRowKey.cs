using System.Globalization;

namespace YieldDataLogger.Api.Storage.Tables;

/// <summary>
/// RowKey encoding for price ticks in Azure Table Storage.
///
/// Table Storage orders rows lexicographically by RowKey within a partition. We want the
/// newest tick to sort FIRST so "give me the last N" is a zero-cost top-N scan. Solution:
/// store (long.MaxValue - tsMicroseconds) as a 19-char zero-padded string. Smaller RowKey =
/// newer tick. Microseconds give enough precision that two consecutive ticks for the same
/// symbol won't collide in practice (and if they do, dedup on identical prices means we
/// don't care).
/// </summary>
public static class TableRowKey
{
    // long.MaxValue is 9223372036854775807 -> 19 digits. Zero-pad to 19 so lexical sort == numeric sort.
    private const int RowKeyWidth = 19;

    public static string FromUnixSeconds(double tsUnix)
    {
        var micros = (long)(tsUnix * 1_000_000d);
        var inverted = long.MaxValue - micros;
        return inverted.ToString("D" + RowKeyWidth, CultureInfo.InvariantCulture);
    }

    public static double ToUnixSeconds(string rowKey)
    {
        if (!long.TryParse(rowKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var inverted))
            return double.NaN;
        var micros = long.MaxValue - inverted;
        return micros / 1_000_000d;
    }
}
