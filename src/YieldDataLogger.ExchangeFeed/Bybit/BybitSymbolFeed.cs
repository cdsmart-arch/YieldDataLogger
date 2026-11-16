using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Sierra;

namespace YieldDataLogger.ExchangeFeed.Bybit;

/// <summary>
/// Keeps <c>_{SYMBOL}_BYBIT.scid</c> in the Sierra Data folder complete: one record per Bybit
/// trade with volume and aggressor side (Buy = taker lifted the ask -> AskVolume, Sell -> BidVolume).
///
/// One task owns the file; the WebSocket only feeds a queue. Sequence:
///   1. History: complete UTC days from Bybit's daily archive (public.bybit.com).
///   2. Gap fill up to now: recent-trade REST (last 1000 trades) when it reaches back to the
///      last record, otherwise minute klines (bars, no side) up to the first recent trade.
///      Every day touched by klines is marked "dirty".
///   3. Live WebSocket trades. After each reconnect step 2 runs again.
///   4. Repair: every 30 min, once the archive for a past day exists, any day that is dirty
///      (or that the file doesn't fully reach) is replaced with the archive's trades. Days
///      recorded live without a gap are left alone.
/// State (archived-through date, dirty days) lives in data\{SYMBOL}.state.json.
/// </summary>
public sealed class BybitSymbolFeed
{
    private const int MaxRunDays = 30;
    private const long MinuteMs = 60_000;
    private static readonly TimeSpan ArchiveCheckEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxKlineGap = TimeSpan.FromDays(3);
    private const int SeenIdLimit = 50_000;

    private readonly string _symbol;
    private readonly BybitConfig _cfg;
    private readonly BybitClient _client;
    private readonly string _dataDir;
    private readonly ILogger _logger;

    private ScidTradeFile? _file;
    private FeedState _state = new();
    private BybitTradeStream? _stream;

    // Dedupe across REST/WS overlaps: never write a trade older than the newest written one,
    // or one whose id was already written.
    private long _lastRawUs = long.MinValue;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly Dictionary<(float Price, uint Volume, bool Buy), int> _boundary = new();
    private long _boundaryMs = long.MinValue;

    private volatile string _phase = "Starting";
    private volatile string? _progress;
    private volatile string? _lastError;
    private long _tradesWritten;
    private DateTime? _lastTradeUtc;

    public BybitSymbolFeed(string symbol, string sierraFolder, BybitConfig cfg, BybitClient client, string dataDir, ILogger logger)
    {
        _symbol  = symbol;
        _cfg     = cfg;
        _client  = client;
        _dataDir = dataDir;
        _logger  = logger;
        ScidPath = Path.Combine(sierraFolder, $"_{symbol}_BYBIT.scid");
    }

    public string ScidPath { get; }
    private string StatePath => Path.Combine(_dataDir, $"{_symbol}.state.json");
    private string CacheDir  => Path.Combine(_dataDir, "cache");

    public FeedStatus Snapshot() => new()
    {
        Exchange          = "Bybit",
        Symbol            = _symbol,
        File              = ScidPath,
        Phase             = _phase,
        Progress          = _progress,
        Connected         = _stream?.Connected ?? false,
        LastTradeUtc      = _lastTradeUtc,
        TradesWritten     = Interlocked.Read(ref _tradesWritten),
        Records           = _file?.Count ?? 0,
        ArchivedThrough   = _state.ArchivedThrough?.ToString("yyyy-MM-dd"),
        PendingRepairDays = _state.Dirty.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd")).ToArray(),
        LastError         = _lastError ?? _stream?.LastError,
    };

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _phase = "Error - retrying in 30s";
                _logger.LogError(ex, "Bybit feed {Symbol} failed; retrying in 30s", _symbol);
                ErrorLog.Append($"Bybit feed {_symbol} failed: {ex}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        _phase = "Starting";
        _file = new ScidTradeFile(ScidPath, Path.Combine(_dataDir, "tmp"), _logger);
        _file.Init();

        var loaded = FeedState.Load(StatePath);
        if (loaded is null || _file.Count == 0
            || !string.Equals(loaded.ScidPath, ScidPath, StringComparison.OrdinalIgnoreCase))
        {
            if (loaded is not null) _logger.LogInformation("Bybit {Symbol}: starting {File} from scratch", _symbol, ScidPath);
            loaded = new FeedState { ScidPath = ScidPath };
        }
        _state = loaded;
        SaveState();

        _seen.Clear();
        _seenOrder.Clear();
        _lastRawUs = FileEndUnixUs();
        SeedBoundary();

        var launch = await _client.GetLaunchDayAsync(_symbol, ct);

        var queue = Channel.CreateUnbounded<FeedItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _stream = new BybitTradeStream(_symbol, _logger);
        using var wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Start live first so trades queue up while history loads; dedupe drops the overlap.
        var wsTask = _stream.RunAsync(queue.Writer, wsCts.Token);
        try
        {
            await CatchUpArchiveAsync(launch, ct);
            await FillGapAsync(ct);
            _lastError = null;
            _phase = "Live";
            _progress = null;

            var nextArchiveCheck = DateTime.UtcNow + ArchiveCheckEvery;
            while (true)
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(TimeSpan.FromMinutes(1));
                    try { await queue.Reader.WaitToReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* periodic wake */ }
                }
                ct.ThrowIfCancellationRequested();

                var batch = new List<BybitTrade>();
                while (queue.Reader.TryRead(out var item))
                {
                    if (item.Subscribed)
                    {
                        WriteTrades(batch);
                        batch.Clear();
                        await FillGapAsync(ct);
                    }
                    else batch.AddRange(item.Trades!);
                }
                WriteTrades(batch);

                if (DateTime.UtcNow >= nextArchiveCheck)
                {
                    if (ArchivePending)
                    {
                        await CatchUpArchiveAsync(launch, ct);
                        _phase = "Live";
                        _progress = null;
                    }
                    nextArchiveCheck = DateTime.UtcNow + ArchiveCheckEvery;
                }
            }
        }
        finally
        {
            wsCts.Cancel();
            try { await wsTask; } catch { /* cancelled */ }
        }
    }

    private bool ArchivePending =>
        _state.ArchivedThrough is null || _state.ArchivedThrough < UtcToday.AddDays(-1);

    // ---------------------------------------------------------------- history / repair

    private async Task CatchUpArchiveAsync(DateOnly launch, CancellationToken ct)
    {
        var yesterday = UtcToday.AddDays(-1);
        var desiredStart = _cfg.HistoryDays <= 0
            ? launch
            : Max(launch, UtcToday.AddDays(-_cfg.HistoryDays));
        if (desiredStart > yesterday) return;

        _phase = "Loading history";

        // HistoryDays was raised: extend the file backwards (records after the range are kept).
        if (_state.ArchivedFrom is { } from && desiredStart < from)
        {
            await ProcessDaysAsync(desiredStart, from.AddDays(-1), force: true, advance: false, ct);
            _state.ArchivedFrom = desiredStart;
            SaveState();
        }

        var first = _state.ArchivedThrough?.AddDays(1) ?? desiredStart;
        var force = _state.ArchivedThrough is null;   // first run: every day comes from the archive
        _state.ArchivedFrom ??= desiredStart;
        await ProcessDaysAsync(first, yesterday, force, advance: true, ct);
    }

    private async Task ProcessDaysAsync(DateOnly first, DateOnly last, bool force, bool advance, CancellationToken ct)
    {
        var run = new List<(DateOnly Day, string File)>();
        var total = last.DayNumber - first.DayNumber + 1;
        var i = 0;
        try
        {
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                ct.ThrowIfCancellationRequested();
                i++;
                _progress = $"{d:yyyy-MM-dd} ({i} of {total})";

                var needs = force
                    || _state.Dirty.Contains(d)
                    || _file!.LastUs < DayStartSierraUs(d.AddDays(1));   // file doesn't reach past this day
                if (!needs)
                {
                    Flush(run, advance);
                    MarkDone(d, advance);
                    continue;
                }

                var gz = Path.Combine(CacheDir, $"{_symbol}{d:yyyy-MM-dd}.csv.gz");
                if (!await _client.DownloadDayAsync(_symbol, d, gz, ct))
                {
                    Flush(run, advance);
                    if (d >= UtcToday.AddDays(-1))
                    {
                        _logger.LogInformation("Bybit {Symbol}: archive for {Day} not published yet", _symbol, d);
                        break;
                    }
                    _logger.LogWarning("Bybit {Symbol}: no archive for {Day}; keeping what the file has", _symbol, d);
                    MarkDone(d, advance);
                    continue;
                }

                run.Add((d, gz));
                if (run.Count >= MaxRunDays) Flush(run, advance);
            }
            Flush(run, advance);
        }
        finally
        {
            foreach (var r in run) TryDelete(r.File);
            SaveState();
        }
    }

    /// <summary>Splices a contiguous run of archive days into the file in one pass.</summary>
    private void Flush(List<(DateOnly Day, string File)> run, bool advance)
    {
        if (run.Count == 0) return;
        long trades = 0;
        var records = run.SelectMany(r => BybitClient.ReadArchive(r.File)).Select(t => { trades++; return ToRec(t); });
        var removed = _file!.ReplaceRange(DayStartSierraUs(run[0].Day), DayStartSierraUs(run[^1].Day.AddDays(1)), records);

        foreach (var r in run)
        {
            MarkDone(r.Day, advance);
            TryDelete(r.File);
        }
        SaveState();
        _lastRawUs = Math.Max(_lastRawUs, FileEndUnixUs());
        _logger.LogInformation("Bybit {Symbol}: archive {First}..{Last} -> {Trades} trades (replaced {Removed} records)",
            _symbol, run[0].Day, run[^1].Day, trades, removed);
        run.Clear();
    }

    private void MarkDone(DateOnly day, bool advance)
    {
        _state.Dirty.Remove(day);
        if (advance && (_state.ArchivedThrough is null || day > _state.ArchivedThrough)) _state.ArchivedThrough = day;
    }

    // ---------------------------------------------------------------- gap fill

    /// <summary>Brings the file up to now via REST after startup or a WebSocket reconnect.</summary>
    private async Task FillGapAsync(CancellationToken ct)
    {
        var recent = await _client.GetRecentTradesAsync(_symbol, ct);
        if (recent.Count == 0) return;

        var empty  = _lastRawUs == long.MinValue;
        var fromMs = empty ? DayStartUnixMs(UtcToday) : _lastRawUs / 1000;

        // recent-trade reaches back to what we have: nothing was missed.
        if (recent[0].UnixUs / 1000 <= fromMs)
        {
            WriteTrades(recent);
            return;
        }

        var prevPhase = _phase;
        _phase = "Filling gap";
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // First whole minute not yet in the file (the last record's minute is partly there).
        var m0 = empty ? fromMs : (fromMs / MinuteMs + 1) * MinuteMs;
        m0 = Math.Max(m0, (nowMs - (long)MaxKlineGap.TotalMilliseconds) / MinuteMs * MinuteMs);
        // First minute boundary at/after the oldest trade recent-trade still has.
        var firstRecentMs = recent[0].UnixUs / 1000;
        var m1 = (firstRecentMs + MinuteMs - 1) / MinuteMs * MinuteMs;

        // The archive will replace these days once it is published. Saved before writing, so a
        // crash mid-fill can't leave an unrecorded approximation in the file.
        for (var d = DayOfMs(fromMs); d <= DayOfMs(Math.Max(fromMs, m1 - 1)); d = d.AddDays(1))
            if (_state.ArchivedThrough is null || d > _state.ArchivedThrough) _state.Dirty.Add(d);
        SaveState();

        var tradesFromUs = long.MinValue;
        if (m1 > m0)
        {
            var bars = await _client.GetMinuteBarsAsync(_symbol, m0, m1 - MinuteMs, ct);
            var recs = bars.Where(b => b.StartMs >= m0 && b.StartMs + MinuteMs <= m1 && b.Volume > 0)
                           .Select(BarRec).ToList();
            if (recs.Count > 0)
            {
                _file!.Append(recs);
                _lastRawUs = Math.Max(_lastRawUs, bars[^1].StartMs * 1000);
            }
            tradesFromUs = m1 * 1000;   // the last bar already covers recent trades before m1
            _logger.LogInformation("Bybit {Symbol}: gap {From:u} -> {To:u} filled with {Bars} minute bars (repaired from the archive later)",
                _symbol, DateTimeOffset.FromUnixTimeMilliseconds(m0).UtcDateTime,
                DateTimeOffset.FromUnixTimeMilliseconds(m1).UtcDateTime, recs.Count);
        }
        WriteTrades(recent.Where(t => t.UnixUs >= tradesFromUs).ToList());
        _phase = prevPhase;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// After a restart the trade ids already written are unknown, and the REST overlap
    /// re-sends trades from the file's last millisecond (Bybit stamps trades in ms, and one ms
    /// often holds several fills). Remember what that millisecond already holds - by price,
    /// volume and side - so those trades aren't written twice while any other fills in the
    /// same millisecond still get through.
    /// </summary>
    private void SeedBoundary()
    {
        _boundary.Clear();
        _boundaryMs = long.MinValue;
        if (_file is not { Count: > 0 }) return;
        var lastMs = ScidFormat.SierraUsToUnixUs(_file.LastUs) / 1000;
        foreach (var r in _file.ReadRange(ScidFormat.UnixUsToSierraUs(lastMs * 1000), long.MaxValue))
        {
            if (r.BidVolume == 0 && r.AskVolume == 0) continue;          // a gap-fill bar, not a trade
            var key = (r.Close, r.Volume, r.AskVolume > 0);
            _boundary[key] = _boundary.GetValueOrDefault(key) + 1;
        }
        _boundaryMs = lastMs;
    }

    private bool AlreadyAtBoundary(BybitTrade t)
    {
        if (_boundary.Count == 0 || t.UnixUs / 1000 != _boundaryMs) return false;
        var key = ((float)t.Price, Volume(t.Size), t.IsBuy);
        if (!_boundary.TryGetValue(key, out var n)) return false;
        if (n <= 1) _boundary.Remove(key); else _boundary[key] = n - 1;
        return true;
    }

    private void WriteTrades(IReadOnlyList<BybitTrade> trades)
    {
        if (trades.Count == 0) return;
        var recs = new List<ScidRec>(trades.Count);
        // Stable sort: the dedupe below relies on time order, which Bybit doesn't promise per message.
        foreach (var t in trades.OrderBy(x => x.UnixUs))
        {
            if (_seen.Contains(t.Id)) continue;
            // Compare in whole milliseconds (Bybit's resolution): a file end read back after a
            // restart may sit a few µs past its trade's millisecond because of collision nudges.
            var tooOld   = _lastRawUs != long.MinValue && t.UnixUs / 1000 < _lastRawUs / 1000;
            if (tooOld || AlreadyAtBoundary(t))
            {
                Remember(t.Id);
                continue;
            }
            recs.Add(ToRec(t));
            _lastRawUs = Math.Max(_lastRawUs, t.UnixUs);
            Remember(t.Id);
        }
        if (recs.Count == 0) return;
        _file!.Append(recs);
        Interlocked.Add(ref _tradesWritten, recs.Count);
        _lastTradeUtc = DateTime.UnixEpoch.AddTicks(_lastRawUs * 10);
    }

    private void Remember(string id)
    {
        if (!_seen.Add(id)) return;
        _seenOrder.Enqueue(id);
        if (_seenOrder.Count > SeenIdLimit) _seen.Remove(_seenOrder.Dequeue());
    }

    private ScidRec ToRec(BybitTrade t)
    {
        var p = (float)t.Price;
        var v = Volume(t.Size);
        return new ScidRec(ScidFormat.UnixUsToSierraUs(t.UnixUs), p, p, p, p, 1, v,
            BidVolume: t.IsBuy ? 0u : v,
            AskVolume: t.IsBuy ? v : 0u);
    }

    /// <summary>A kline (gap fill only): a bar record with no side; replaced by trades when repaired.</summary>
    private ScidRec BarRec(BybitBar b) => new(
        ScidFormat.UnixUsToSierraUs(b.StartMs * 1000),
        (float)b.Open, (float)b.High, (float)b.Low, (float)b.Close,
        1, Volume(b.Volume), 0, 0);

    private uint Volume(double size) =>
        (uint)Math.Clamp(Math.Round(size * Math.Max(1, _cfg.VolumeMultiplier)), 1d, uint.MaxValue);

    // ---------------------------------------------------------------- helpers

    private long FileEndUnixUs() =>
        _file is { Count: > 0 } f ? ScidFormat.SierraUsToUnixUs(f.LastUs) : long.MinValue;

    private void SaveState()
    {
        try { _state.Save(StatePath); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not save {Path}", StatePath); }
    }

    private static DateOnly UtcToday => DateOnly.FromDateTime(DateTime.UtcNow);
    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;
    private static long DayStartUnixMs(DateOnly d) =>
        new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static long DayStartSierraUs(DateOnly d) => ScidFormat.UnixUsToSierraUs(DayStartUnixMs(d) * 1000);
    private static DateOnly DayOfMs(long ms) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* cache, harmless */ }
    }
}

/// <summary>Persisted per-symbol progress.</summary>
public sealed class FeedState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The .scid this state belongs to; a different path (new Sierra folder) starts over.</summary>
    public string ScidPath { get; set; } = "";

    /// <summary>First UTC day loaded from the archive.</summary>
    public DateOnly? ArchivedFrom { get; set; }

    /// <summary>Every day up to and including this one is complete (from the archive, or recorded live without a gap).</summary>
    public DateOnly? ArchivedThrough { get; set; }

    /// <summary>Days after <see cref="ArchivedThrough"/> that contain gap-fill bars and need the archive.</summary>
    public HashSet<DateOnly> Dirty { get; set; } = new();

    public static FeedState? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FeedState>(File.ReadAllText(path), Json) : null;
        }
        catch { return null; }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>One feed's line in status.json.</summary>
public sealed record FeedStatus
{
    public string Exchange { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string File { get; init; } = "";
    public string Phase { get; init; } = "";
    public string? Progress { get; init; }
    public bool Connected { get; init; }
    public DateTime? LastTradeUtc { get; init; }
    public long TradesWritten { get; init; }
    public long Records { get; init; }
    public string? ArchivedThrough { get; init; }
    public string[] PendingRepairDays { get; init; } = [];
    public string? LastError { get; init; }
}
