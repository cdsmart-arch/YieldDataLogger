using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Sources;

/// <summary>
/// Abstraction over the investing.com streaming source. Today we have a direct Socket.IO
/// implementation (<see cref="InvestingSocketClient"/>); a Playwright-driven fallback that
/// loads the vendor's own jQuery bundle in headless Chromium will plug in here if the
/// direct client cannot handshake with the (very old) forexpros endpoint.
/// </summary>
public interface IInvestingStreamClient : IAsyncDisposable
{
    event Func<PriceTick, CancellationToken, ValueTask>? OnTick;

    /// <summary>True when the client is connected and has successfully subscribed to at least one pid.</summary>
    bool IsLive { get; }

    /// <summary>UTC time of the most recent successfully-dispatched tick. DateTime.MinValue if none yet.</summary>
    DateTime LastTickUtc { get; }

    Task StartAsync(IEnumerable<int> pids, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
