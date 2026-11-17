using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Binance;

/// <summary>
/// Where a Binance market lives. USDⓈ-M futures and spot share the aggTrade data model and
/// differ only in hosts, paths, rate budgets and file naming.
/// </summary>
public sealed record BinanceMarket(
    string Exchange,            // display name and .scid suffix (upper-cased)
    string RestBase,
    string AggTradesPath,
    string ArchiveBase,
    string WebSocketBase,       // + "{symbol lowercase}@aggTrade"
    bool IsFutures,
    TimeSpan MinInterval,       // pacing between REST calls
    int WeightSoftLimit,        // back off for the rest of the minute above this used weight
    TimeSpan MaxRestGap)        // furthest back a startTime search may go; older gaps wait for the archive
{
    /// <summary>
    /// USDⓈ-M perpetuals. aggTrades costs 20 weight of the 2400/min IP budget, so one call per
    /// 600 ms (<= 2000/min). Since the 2026 routing change aggTrade streams live under /market.
    /// A startTime search more than 2 days back is refused (400, code -4166 "Search window is
    /// restricted to recent 2 days only"), so stay an hour inside that.
    /// </summary>
    public static readonly BinanceMarket UsdMFutures = new(
        "Binance", "https://fapi.binance.com", "/fapi/v1/aggTrades",
        "https://data.binance.vision/data/futures/um/daily/aggTrades",
        "wss://fstream.binance.com/market/ws/", IsFutures: true,
        TimeSpan.FromMilliseconds(600), 2000, TimeSpan.FromHours(47));

    /// <summary>Spot. aggTrades costs 4 weight of the 6000/min IP budget; no search-window limit seen.</summary>
    public static readonly BinanceMarket Spot = new(
        "BinanceSpot", "https://api.binance.com", "/api/v3/aggTrades",
        "https://data.binance.vision/data/spot/daily/aggTrades",
        "wss://stream.binance.com:9443/ws/", IsFutures: false,
        TimeSpan.FromMilliseconds(200), 5000, TimeSpan.FromDays(3));
}

/// <summary>
/// Binance public market data - no API key. Daily aggTrades archives from data.binance.vision,
/// and aggTrades REST (paged forward by id) for everything since.
///
/// One aggTrade bundles the fills of a single taker order at one price, so volume and side
/// totals equal the raw trades' while the record count is ~3x smaller. is_buyer_maker = true
/// means the buyer was resting, i.e. an aggressive SELLER -> bid volume.
/// </summary>
public sealed class BinanceClient
{
    // Binance rate-limits per IP and per market (spot and futures are separate budgets), so
    // pacing is shared by every feed of the same market in this process.
    private sealed class Pace { public readonly SemaphoreSlim Gate = new(1, 1); public DateTime NextUtc = DateTime.MinValue; }
    private static readonly ConcurrentDictionary<string, Pace> Paces = new();

    private readonly HttpClient _http;
    private readonly Pace _pace;

    public BinanceClient(HttpClient http, BinanceMarket market)
    {
        _http  = http;
        Market = market;
        _pace  = Paces.GetOrAdd(market.RestBase, _ => new Pace());
    }

    public BinanceMarket Market { get; }

    /// <summary>Launch day and LOT_SIZE step.</summary>
    public async Task<SymbolInfo> GetSymbolInfoAsync(string symbol, CancellationToken ct)
    {
        var url = Market.IsFutures
            ? $"{Market.RestBase}/fapi/v1/exchangeInfo"
            : $"{Market.RestBase}/api/v3/exchangeInfo?symbol={symbol}";
        double step = 1;
        long? onboardMs = null;
        var found = false;
        using (var doc = await GetJsonAsync(url, ct))
        {
            foreach (var s in doc.RootElement.GetProperty("symbols").EnumerateArray())
            {
                if (s.GetProperty("symbol").GetString() != symbol) continue;
                found = true;
                if (s.TryGetProperty("onboardDate", out var od)) onboardMs = od.GetInt64();
                foreach (var f in s.GetProperty("filters").EnumerateArray())
                    if (f.GetProperty("filterType").GetString() == "LOT_SIZE")
                        step = double.Parse(f.GetProperty("stepSize").GetString()!, CultureInfo.InvariantCulture);
                break;
            }
        }
        if (!found) throw new InvalidOperationException($"{Market.Exchange} has no symbol {symbol}.");

        // Spot has no onboard date: the oldest aggTrade Binance still serves stands in for it.
        if (onboardMs is null)
        {
            var first = await GetAggTradesFromIdAsync(symbol, 0, ct);
            onboardMs = first.Count > 0 ? first[0].Trade.UnixUs / 1000 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        return new SymbolInfo(DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(onboardMs.Value).UtcDateTime), step);
    }

    /// <summary>Downloads one UTC day's aggTrades zip. False when there is none (yet) for that day (404).</summary>
    public async Task<bool> DownloadDayAsync(string symbol, DateOnly day, string destination, CancellationToken ct)
    {
        var url = $"{Market.ArchiveBase}/{symbol}/{symbol}-aggTrades-{day:yyyy-MM-dd}.zip";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            await src.CopyToAsync(dst, ct);
        File.Move(tmp, destination, overwrite: true);
        return true;
    }

    /// <summary>
    /// Reads an aggTrades zip into time order. Tolerates the format differences Binance has
    /// used: with or without a header row, transact_time in ms (13 digits) or µs (16 digits),
    /// booleans as true/True, and extra trailing columns (spot's is_best_match).
    /// </summary>
    public static List<ExchangeTrade> ReadArchive(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{zipPath}: no CSV inside.");
        using var reader = new StreamReader(entry.Open());

        int cId = 0, cPrice = 1, cQty = 2, cFirst = 3, cLast = 4, cTime = 5, cMaker = 6;
        var trades = new List<ExchangeTrade>(600_000);
        var first = true;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (first)
            {
                first = false;
                if (!char.IsDigit(f[0], 0))   // header row: map by name
                {
                    int Col(params string[] names)
                    {
                        foreach (var n in names)
                        {
                            var i = Array.IndexOf(f, n);
                            if (i >= 0) return i;
                        }
                        throw new InvalidDataException($"{zipPath}: archive has no '{names[0]}' column.");
                    }
                    cId = Col("agg_trade_id"); cPrice = Col("price"); cQty = Col("quantity", "qty");
                    cFirst = Col("first_trade_id"); cLast = Col("last_trade_id");
                    cTime = Col("transact_time"); cMaker = Col("is_buyer_maker");
                    continue;
                }
            }
            trades.Add(new ExchangeTrade(
                ToUnixUs(long.Parse(f[cTime], CultureInfo.InvariantCulture)),
                double.Parse(f[cPrice], CultureInfo.InvariantCulture),
                double.Parse(f[cQty], CultureInfo.InvariantCulture),
                IsBuy: !string.Equals(f[cMaker], "true", StringComparison.OrdinalIgnoreCase),
                f[cId],
                Fills(f[cFirst], f[cLast])));
        }
        return trades.OrderBy(t => t.UnixUs).ToList();   // stable: keeps id order within a timestamp
    }

    /// <summary>Archive timestamps are ms (futures) or µs (spot since 2025): tell them apart by size.</summary>
    internal static long ToUnixUs(long t) => t >= 100_000_000_000_000L ? t : t * 1000;

    /// <summary>Up to 1000 aggTrades starting at <paramref name="fromId"/>, ascending.</summary>
    public Task<List<(long Id, ExchangeTrade Trade)>> GetAggTradesFromIdAsync(string symbol, long fromId, CancellationToken ct) =>
        GetAggTradesAsync($"{Market.RestBase}{Market.AggTradesPath}?symbol={symbol}&fromId={fromId}&limit=1000", ct);

    /// <summary>Up to 1000 aggTrades in [startMs, endMs] (Binance allows at most a 1 hour window), ascending.</summary>
    public Task<List<(long Id, ExchangeTrade Trade)>> GetAggTradesInWindowAsync(string symbol, long startMs, long endMs, CancellationToken ct) =>
        GetAggTradesAsync($"{Market.RestBase}{Market.AggTradesPath}?symbol={symbol}&startTime={startMs}&endTime={endMs}&limit=1000", ct);

    private async Task<List<(long Id, ExchangeTrade Trade)>> GetAggTradesAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        var list = new List<(long, ExchangeTrade)>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var id = t.GetProperty("a").GetInt64();
            list.Add((id, new ExchangeTrade(
                ToUnixUs(t.GetProperty("T").GetInt64()),
                double.Parse(t.GetProperty("p").GetString()!, CultureInfo.InvariantCulture),
                double.Parse(t.GetProperty("q").GetString()!, CultureInfo.InvariantCulture),
                IsBuy: !t.GetProperty("m").GetBoolean(),
                id.ToString(CultureInfo.InvariantCulture),
                Fills(t.GetProperty("f").GetInt64(), t.GetProperty("l").GetInt64()))));
        }
        return list;
    }

    public static uint Fills(long first, long last) => (uint)Math.Clamp(last - first + 1, 1, uint.MaxValue);
    private static uint Fills(string first, string last) =>
        Fills(long.Parse(first, CultureInfo.InvariantCulture), long.Parse(last, CultureInfo.InvariantCulture));

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _pace.Gate.WaitAsync(ct);
            HttpResponseMessage resp;
            try
            {
                var wait = _pace.NextUtc - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                resp = await _http.GetAsync(url, ct);
                _pace.NextUtc = DateTime.UtcNow + Market.MinInterval;

                // Over the limit (429) or IP-banned for ignoring it (418): back off as told.
                if ((int)resp.StatusCode is 429 or 418 && attempt < 5)
                {
                    var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                    _pace.NextUtc = DateTime.UtcNow + retry;
                    resp.Dispose();
                    continue;
                }
                // Getting close to the minute budget (e.g. other software on this IP): slow down.
                if (resp.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var used)
                    && int.TryParse(used.FirstOrDefault(), out var w) && w > Market.WeightSoftLimit)
                    _pace.NextUtc = DateTime.UtcNow + TimeSpan.FromSeconds(61 - DateTime.UtcNow.Second);
            }
            finally
            {
                _pace.Gate.Release();
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    // Binance explains refusals in the body ({"code":-4166,"msg":...}); keep it in the error.
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    if (body.Length > 300) body = body[..300];
                    throw new HttpRequestException(
                        $"{Market.Exchange} {(int)resp.StatusCode} {resp.ReasonPhrase} for {url}: {body}", null, resp.StatusCode);
                }
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(s, cancellationToken: ct);
            }
        }
    }
}
