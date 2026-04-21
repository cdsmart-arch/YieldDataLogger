using YieldDataLogger.Api.Controllers;

namespace YieldDataLogger.Api.Storage;

/// <summary>
/// Read-side abstraction over the active storage backend. Controllers talk to this; the
/// concrete implementation (Tables or SQL) is chosen at startup based on Storage:Backend.
/// Separating reads from writes means the REST API stays agnostic while sinks can still
/// fan out to multiple backends during a migration.
/// </summary>
public interface IPriceHistoryReader
{
    /// <summary>
    /// Returns up to <paramref name="take"/> most-recent ticks for <paramref name="symbol"/>,
    /// newest first. When <paramref name="fromTs"/>/<paramref name="toTs"/> are supplied (unix
    /// seconds), restricts to that window.
    /// </summary>
    Task<IReadOnlyList<PriceTickDto>> GetHistoryAsync(
        string symbol,
        double? fromTs,
        double? toTs,
        int take,
        CancellationToken ct);

    Task<PriceTickDto?> GetLatestAsync(string symbol, CancellationToken ct);
}
