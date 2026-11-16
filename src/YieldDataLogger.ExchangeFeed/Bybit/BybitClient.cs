using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using YieldDataLogger.ExchangeFeed.Feeds;

namespace YieldDataLogger.ExchangeFeed.Bybit;

/// <summary>
/// Bybit public market data - no API key. Daily trade archives from public.bybit.com, and
/// recent trades + minute klines from the v5 REST API for the part of today the archive
/// doesn't cover yet.
/// </summary>
public sealed class BybitClient
{
    private const string Api     = "https://api.bybit.com";
    private const string Archive = "https://public.bybit.com/trading";

    private readonly HttpClient _http;

    public BybitClient(HttpClient http) { _http = http; }

    /// <summary>Launch day (first archive file) and quantity step of a linear contract.</summary>
    public async Task<SymbolInfo> GetSymbolInfoAsync(string symbol, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Api}/v5/market/instruments-info?category=linear&symbol={symbol}", ct);
        var list = doc.RootElement.GetProperty("result").GetProperty("list");
        if (list.GetArrayLength() == 0) throw new InvalidOperationException($"Bybit has no linear symbol {symbol}.");
        var ms = long.Parse(list[0].GetProperty("launchTime").GetString()!, CultureInfo.InvariantCulture);
        var step = double.Parse(list[0].GetProperty("lotSizeFilter").GetProperty("qtyStep").GetString()!, CultureInfo.InvariantCulture);
        return new SymbolInfo(DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime), step);
    }

    /// <summary>Downloads one UTC day's trade archive (.csv.gz). False when Bybit has no file for that day (404).</summary>
    public async Task<bool> DownloadDayAsync(string symbol, DateOnly day, string destination, CancellationToken ct)
    {
        var url = $"{Archive}/{symbol}/{symbol}{day:yyyy-MM-dd}.csv.gz";
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

    /// <summary>Reads an archive file into time order (stable, so same-timestamp fills keep their order).</summary>
    public static List<ExchangeTrade> ReadArchive(string gzPath)
    {
        using var fs = new FileStream(gzPath, FileMode.Open, FileAccess.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);

        var header = (reader.ReadLine() ?? "").Split(',');
        int Col(string name)
        {
            var i = Array.IndexOf(header, name);
            if (i < 0) throw new InvalidDataException($"{gzPath}: archive has no '{name}' column.");
            return i;
        }
        int cTs = Col("timestamp"), cSide = Col("side"), cSize = Col("size"), cPrice = Col("price"), cId = Col("trdMatchID");

        var trades = new List<ExchangeTrade>(150_000);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');
            trades.Add(new ExchangeTrade(
                (long)Math.Round(double.Parse(f[cTs], CultureInfo.InvariantCulture) * 1_000_000d),
                double.Parse(f[cPrice], CultureInfo.InvariantCulture),
                double.Parse(f[cSize], CultureInfo.InvariantCulture),
                f[cSide] == "Buy",
                f[cId]));
        }
        return trades.OrderBy(t => t.UnixUs).ToList();
    }

    /// <summary>The most recent trades (Bybit returns at most 1000), oldest first.</summary>
    public async Task<List<ExchangeTrade>> GetRecentTradesAsync(string symbol, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{Api}/v5/market/recent-trade?category=linear&symbol={symbol}&limit=1000", ct);
        var list = new List<ExchangeTrade>();
        foreach (var t in doc.RootElement.GetProperty("result").GetProperty("list").EnumerateArray())
        {
            list.Add(new ExchangeTrade(
                long.Parse(t.GetProperty("time").GetString()!, CultureInfo.InvariantCulture) * 1000,
                double.Parse(t.GetProperty("price").GetString()!, CultureInfo.InvariantCulture),
                double.Parse(t.GetProperty("size").GetString()!, CultureInfo.InvariantCulture),
                t.GetProperty("side").GetString() == "Buy",
                t.GetProperty("execId").GetString()!));
        }
        list.Reverse();                                   // API is newest first
        return list.OrderBy(t => t.UnixUs).ToList();
    }

    /// <summary>One-minute klines whose start is in [startMs, endMs], oldest first.</summary>
    public async Task<List<GapBar>> GetMinuteBarsAsync(string symbol, long startMs, long endMs, CancellationToken ct)
    {
        var bars = new Dictionary<long, GapBar>();
        var end = endMs;
        while (end >= startMs)
        {
            using var doc = await GetJsonAsync(
                $"{Api}/v5/market/kline?category=linear&symbol={symbol}&interval=1&start={startMs}&end={end}&limit=1000", ct);
            var page = doc.RootElement.GetProperty("result").GetProperty("list");
            long oldest = long.MaxValue;
            var n = 0;
            foreach (var row in page.EnumerateArray())
            {
                double D(int i) => double.Parse(row[i].GetString()!, CultureInfo.InvariantCulture);
                var start = long.Parse(row[0].GetString()!, CultureInfo.InvariantCulture);
                if (start >= startMs && start <= endMs)
                    bars[start] = new GapBar(start, D(1), D(2), D(3), D(4), D(5));
                oldest = Math.Min(oldest, start);
                n++;
            }
            if (n < 1000 || oldest <= startMs) break;
            end = oldest - 1;
        }
        return bars.Values.OrderBy(b => b.StartMs).ToList();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        if (doc.RootElement.TryGetProperty("retCode", out var rc) && rc.GetInt32() != 0)
        {
            var msg = doc.RootElement.TryGetProperty("retMsg", out var m) ? m.GetString() : "?";
            doc.Dispose();
            throw new HttpRequestException($"Bybit error {rc.GetInt32()} ({msg}) for {url}");
        }
        return doc;
    }
}
