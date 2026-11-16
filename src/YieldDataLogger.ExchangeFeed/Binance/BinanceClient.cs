using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Binance;

/// <summary>
/// Binance USDⓈ-M futures public market data - no API key. Daily aggTrades archives from
/// data.binance.vision, and aggTrades REST (paged forward by id) for everything since.
///
/// One aggTrade bundles the fills of a single taker order at one price, so volume and side
/// totals equal the raw trades' while the record count is ~3x smaller. is_buyer_maker = true
/// means the buyer was resting, i.e. an aggressive SELLER -> bid volume.
/// </summary>
public sealed class BinanceClient
{
    private const string Api     = "https://fapi.binance.com";
    private const string Archive = "https://data.binance.vision/data/futures/um/daily/aggTrades";

    // aggTrades costs 20 weight; the IP limit is 2400/min. One request per 600 ms keeps all
    // Binance feeds in this process together at <= 2000/min.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(600);
    private static DateTime _nextRequestUtc = DateTime.MinValue;

    private readonly HttpClient _http;

    public BinanceClient(HttpClient http) { _http = http; }

    /// <summary>Onboard day and LOT_SIZE step of a USDⓈ-M contract.</summary>
    public async Task<SymbolInfo> GetSymbolInfoAsync(string symbol, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Api}/fapi/v1/exchangeInfo", ct);
        foreach (var s in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (s.GetProperty("symbol").GetString() != symbol) continue;
            var onboard = s.GetProperty("onboardDate").GetInt64();
            var step = 1d;
            foreach (var f in s.GetProperty("filters").EnumerateArray())
                if (f.GetProperty("filterType").GetString() == "LOT_SIZE")
                    step = double.Parse(f.GetProperty("stepSize").GetString()!, CultureInfo.InvariantCulture);
            return new SymbolInfo(DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(onboard).UtcDateTime), step);
        }
        throw new InvalidOperationException($"Binance USD-M has no symbol {symbol}.");
    }

    /// <summary>Downloads one UTC day's aggTrades zip. False when there is none (yet) for that day (404).</summary>
    public async Task<bool> DownloadDayAsync(string symbol, DateOnly day, string destination, CancellationToken ct)
    {
        var url = $"{Archive}/{symbol}/{symbol}-aggTrades-{day:yyyy-MM-dd}.zip";
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
    /// Reads an aggTrades zip (one CSV; newer files have a header row, older ones don't) into
    /// time order.
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
                    int Col(string name)
                    {
                        var i = Array.IndexOf(f, name);
                        if (i < 0) throw new InvalidDataException($"{zipPath}: archive has no '{name}' column.");
                        return i;
                    }
                    cId = Col("agg_trade_id"); cPrice = Col("price"); cQty = Col("quantity");
                    cFirst = Col("first_trade_id"); cLast = Col("last_trade_id");
                    cTime = Col("transact_time"); cMaker = Col("is_buyer_maker");
                    continue;
                }
            }
            trades.Add(new ExchangeTrade(
                long.Parse(f[cTime], CultureInfo.InvariantCulture) * 1000,
                double.Parse(f[cPrice], CultureInfo.InvariantCulture),
                double.Parse(f[cQty], CultureInfo.InvariantCulture),
                IsBuy: !string.Equals(f[cMaker], "true", StringComparison.OrdinalIgnoreCase),
                f[cId],
                Fills(f[cFirst], f[cLast])));
        }
        return trades.OrderBy(t => t.UnixUs).ToList();   // stable: keeps id order within a millisecond
    }

    /// <summary>Up to 1000 aggTrades starting at <paramref name="fromId"/>, ascending.</summary>
    public Task<List<(long Id, ExchangeTrade Trade)>> GetAggTradesFromIdAsync(string symbol, long fromId, CancellationToken ct) =>
        GetAggTradesAsync($"{Api}/fapi/v1/aggTrades?symbol={symbol}&fromId={fromId}&limit=1000", ct);

    /// <summary>Up to 1000 aggTrades in [startMs, endMs] (Binance allows at most a 1 hour window), ascending.</summary>
    public Task<List<(long Id, ExchangeTrade Trade)>> GetAggTradesInWindowAsync(string symbol, long startMs, long endMs, CancellationToken ct) =>
        GetAggTradesAsync($"{Api}/fapi/v1/aggTrades?symbol={symbol}&startTime={startMs}&endTime={endMs}&limit=1000", ct);

    private async Task<List<(long Id, ExchangeTrade Trade)>> GetAggTradesAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);
        var list = new List<(long, ExchangeTrade)>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var id = t.GetProperty("a").GetInt64();
            list.Add((id, new ExchangeTrade(
                t.GetProperty("T").GetInt64() * 1000,
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
            await Gate.WaitAsync(ct);
            HttpResponseMessage resp;
            try
            {
                var wait = _nextRequestUtc - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                resp = await _http.GetAsync(url, ct);
                _nextRequestUtc = DateTime.UtcNow + MinInterval;

                // Over the limit (429) or IP-banned for ignoring it (418): back off as told.
                if ((int)resp.StatusCode is 429 or 418 && attempt < 5)
                {
                    var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                    _nextRequestUtc = DateTime.UtcNow + retry;
                    resp.Dispose();
                    continue;
                }
                // Getting close to the minute budget (e.g. other software on this IP): slow down.
                if (resp.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var used)
                    && int.TryParse(used.FirstOrDefault(), out var w) && w > 2000)
                    _nextRequestUtc = DateTime.UtcNow + TimeSpan.FromSeconds(61 - DateTime.UtcNow.Second);
            }
            finally
            {
                Gate.Release();
            }

            using (resp)
            {
                resp.EnsureSuccessStatusCode();
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(s, cancellationToken: ct);
            }
        }
    }
}
