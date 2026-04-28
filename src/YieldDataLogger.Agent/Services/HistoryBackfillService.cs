using System.Data.SQLite;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Agent.Configuration;

namespace YieldDataLogger.Agent.Services;

/// <summary>
/// Runs a history backfill for every subscribed symbol immediately after the SignalR hub
/// connects (and again on each reconnect, to cover any offline gap).
///
/// For each symbol it:
///   1. Reads the latest TIMESTAMP already in the local SQLite file.
///   2. Calls GET /api/ticks/{symbol}?fromTs={latest}&amp;take=5000 on the REST API.
///   3. Pages through results until the server returns fewer rows than the page size.
///   4. Batch-inserts into SQLite inside a single transaction (fast: &lt;1s for thousands of rows).
///
/// Ticks that are already present are silently ignored (INSERT OR IGNORE on TIMESTAMP).
/// This means the backfill is fully idempotent and safe to re-run.
/// </summary>
public sealed class HistoryBackfillService : BackgroundService
{
    private const int PageSize = 5_000;

    private readonly AgentOptions _options;
    private readonly TickHubClient _hubClient;
    private readonly SubscriptionManager _subscriptions;
    private readonly HttpClient _http;
    private readonly ILogger<HistoryBackfillService> _logger;

    public HistoryBackfillService(
        IOptions<AgentOptions> options,
        TickHubClient hubClient,
        SubscriptionManager subscriptions,
        IHttpClientFactory httpFactory,
        ILogger<HistoryBackfillService> logger)
    {
        _options     = options.Value;
        _hubClient   = hubClient;
        _subscriptions = subscriptions;
        _http        = httpFactory.CreateClient("backfill");
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Sinks.Sqlite.Enabled)
        {
            _logger.LogInformation("SqliteSink disabled – history backfill skipped.");
            return;
        }

        if (_options.HistoryDays <= 0)
        {
            _logger.LogInformation("HistoryDays=0 – history backfill disabled.");
            return;
        }

        var apiBase = DeriveApiBase();
        if (string.IsNullOrWhiteSpace(apiBase))
        {
            _logger.LogWarning("ApiBaseUrl not set and could not be derived from HubUrl – history backfill skipped.");
            return;
        }

        _http.BaseAddress = new Uri(apiBase);
        _http.Timeout     = TimeSpan.FromMinutes(5);

        // Run at below-normal priority so the backfill never starves the rest of the system.
        System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait until the hub reports Connected.
            try { await WaitForConnectionAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (stoppingToken.IsCancellationRequested) return;

            try
            {
                await RunBackfillAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Never let a backfill crash take down the service.
                _logger.LogError(ex, "Unexpected error during history backfill – will retry on next reconnect.");
            }

            // After backfill, wait until the connection drops so we can backfill the gap
            // on the next reconnect.
            try { await WaitForDisconnectAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private async Task RunBackfillAsync(CancellationToken ct)
    {
        var sqlitePath = Environment.ExpandEnvironmentVariables(_options.Sinks.Sqlite.Path);
        var symbols    = _subscriptions.Current;

        _logger.LogInformation(
            "History backfill starting for {Count} symbol(s).", symbols.Count);

        var totalInserted = 0;

        for (int idx = 0; idx < symbols.Count; idx++)
        {
            if (ct.IsCancellationRequested) break;

            var symbol = symbols[idx];
            try
            {
                totalInserted += await BackfillSymbolAsync(sqlitePath, symbol, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backfill failed for symbol {Symbol} – skipping.", symbol);
            }

            // Breathe between symbols so disk/CPU/network aren't fully saturated.
            // The delay is skipped after the last symbol and when only one symbol is subscribed.
            if (_options.BackfillDelayMs > 0 && idx < symbols.Count - 1)
            {
                try { await Task.Delay(_options.BackfillDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation(
            "History backfill complete. {Total} new tick(s) written.", totalInserted);
    }

    private async Task<int> BackfillSymbolAsync(
        string sqlitePath, string symbol, CancellationToken ct)
    {
        var file = Path.Combine(sqlitePath, $"{symbol}.sqlite");
        EnsureTable(file);

        var latestTs = GetLatestTimestamp(file);

        // On a fresh install latestTs == 0, so cap it to HistoryDays ago.
        // For existing files latestTs is recent, so it will be > the cap and wins.
        // Manager-configured value (subscriptions.json) takes precedence over appsettings default.
        var historyDays = _subscriptions.HistoryDays > 0
            ? _subscriptions.HistoryDays
            : _options.HistoryDays;
        var historyFloor = DateTimeOffset.UtcNow
            .AddDays(-historyDays)
            .ToUnixTimeSeconds();

        var inserted = 0;
        var fromTs   = Math.Max(latestTs, historyFloor);
        int fetched;

        do
        {
            List<TickDto>? page;
            try
            {
                var url = $"/api/ticks/{Uri.EscapeDataString(symbol)}?fromTs={fromTs:F0}&take={PageSize}";
                page = await _http.GetFromJsonAsync<List<TickDto>>(url, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backfill HTTP request failed for {Symbol}", symbol);
                break;
            }

            if (page is null || page.Count == 0) break;

            fetched   = page.Count;
            inserted += BulkInsert(file, page);
            fromTs    = page.Max(t => t.TsUnix) + 1;   // advance cursor past what we just got

            if (fetched == PageSize)
                _logger.LogDebug("Backfill {Symbol}: fetched page of {Count}, continuing...", symbol, fetched);
        }
        while (fetched == PageSize && !ct.IsCancellationRequested);

        if (inserted > 0)
            _logger.LogInformation("Backfill {Symbol}: inserted {Count} tick(s) (from ts {From:F0}).",
                symbol, inserted, latestTs);

        return inserted;
    }

    /// <summary>
    /// Returns the latest TIMESTAMP (unix seconds) already in the local SQLite file,
    /// or 0 if the file does not exist / is empty.
    /// </summary>
    private static double GetLatestTimestamp(string file)
    {
        if (!File.Exists(file)) return 0;
        try
        {
            using var cn  = Open(file);
            using var cmd = new SQLiteCommand(
                "SELECT MAX(TIMESTAMP) FROM PriceData", cn);
            var result = cmd.ExecuteScalar();
            return result is DBNull or null ? 0 : Convert.ToDouble(result);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Creates the PriceData table if absent, and ensures a UNIQUE index on TIMESTAMP exists
    /// so INSERT OR IGNORE silently skips duplicate timestamps (makes backfill idempotent).
    ///
    /// On databases written by the original install (before the unique index was introduced)
    /// there may already be duplicate TIMESTAMP rows.  We remove those duplicates first —
    /// keeping the row with the lowest rowid — so the index creation always succeeds.
    /// </summary>
    private static void EnsureTable(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(file)) SQLiteConnection.CreateFile(file);

        using var cn = Open(file);

        using (var cmd = new SQLiteCommand(
            "CREATE TABLE IF NOT EXISTS PriceData (TIMESTAMP real, CLOSE real)", cn))
            cmd.ExecuteNonQuery();

        // Check whether the unique index already exists.
        bool indexExists;
        using (var cmd = new SQLiteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_timestamp'", cn))
            indexExists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

        if (!indexExists)
        {
            // The original install wrote rows without a unique constraint so duplicates may
            // exist.  Delete them (keep the earliest rowid per timestamp) before adding the
            // index, otherwise SQLite will refuse to create it.
            using (var cmd = new SQLiteCommand(
                @"DELETE FROM PriceData
                  WHERE rowid NOT IN (
                      SELECT MIN(rowid) FROM PriceData GROUP BY TIMESTAMP
                  )", cn))
                cmd.ExecuteNonQuery();

            using (var cmd = new SQLiteCommand(
                "CREATE UNIQUE INDEX idx_timestamp ON PriceData (TIMESTAMP)", cn))
                cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Bulk-inserts a page of ticks inside a single transaction.
    /// INSERT OR IGNORE skips any timestamps that are already present.
    /// Returns the number of rows actually written.
    /// </summary>
    private static int BulkInsert(string file, IEnumerable<TickDto> ticks)
    {
        using var cn = Open(file);
        using var tx = cn.BeginTransaction();

        var written = 0;
        using var cmd = new SQLiteCommand(
            "INSERT OR IGNORE INTO PriceData (TIMESTAMP, CLOSE) VALUES (@TS, @CLOSE)", cn, tx);

        var pTs    = cmd.Parameters.Add("@TS",    System.Data.DbType.Double);
        var pClose = cmd.Parameters.Add("@CLOSE", System.Data.DbType.Double);

        foreach (var t in ticks)
        {
            pTs.Value    = t.TsUnix;
            pClose.Value = t.Price;
            written += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return written;
    }

    private static SQLiteConnection Open(string file)
    {
        var cn = new SQLiteConnection($"Data Source={file};Version=3;");
        cn.Open();
        // WAL is persisted in the file header – set once and all future connections inherit it.
        // SYNCHRONOUS=NORMAL and the cache settings are connection-level so we set them every time.
        foreach (var sql in new[]
        {
            "PRAGMA journal_mode = WAL",
            "PRAGMA synchronous  = NORMAL",
            "PRAGMA cache_size   = -4000",
            "PRAGMA temp_store   = MEMORY",
        })
        {
            using var p = new SQLiteCommand(sql, cn);
            p.ExecuteNonQuery();
        }
        return cn;
    }

    private async Task WaitForConnectionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested &&
               _hubClient.ConnectionState != HubConnectionState.Connected)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForDisconnectAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested &&
               _hubClient.ConnectionState == HubConnectionState.Connected)
        {
            await Task.Delay(2_000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the REST API base URL.  Explicit config wins; otherwise strip the
    /// "/hubs/..." suffix from HubUrl (e.g. https://host/hubs/ticks → https://host).
    /// </summary>
    private string DeriveApiBase()
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiBaseUrl))
            return _options.ApiBaseUrl.TrimEnd('/');

        var hub = _options.HubUrl;
        if (string.IsNullOrWhiteSpace(hub)) return string.Empty;

        var idx = hub.IndexOf("/hubs/", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? hub[..idx] : string.Empty;
    }

    // JSON shape returned by GET /api/ticks/{symbol}
    private sealed record TickDto(string Symbol, double TsUnix, double Price, string Source);
}
