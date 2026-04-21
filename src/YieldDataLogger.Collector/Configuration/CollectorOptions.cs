namespace YieldDataLogger.Collector.Configuration;

/// <summary>
/// Root collector configuration bound from appsettings.json.
/// </summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    public string InstrumentsFile { get; set; } = "instruments.json";

    public SinkOptions Sinks { get; set; } = new();
    public CnbcOptions Cnbc { get; set; } = new();
    public InvestingOptions Investing { get; set; } = new();
}

public sealed class SinkOptions
{
    public SqliteSinkOptions Sqlite { get; set; } = new();
    public ScidSinkOptions Scid { get; set; } = new();
}

public sealed class SqliteSinkOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = @"%ProgramData%\YieldDataLogger\Yields";
}

public sealed class ScidSinkOptions
{
    public bool Enabled { get; set; } = false;
    public string Path { get; set; } = @"C:\SierraChart\Data";

    /// <summary>
    /// Canonical symbols permitted to be written as .scid. When empty, all subscribed symbols
    /// are allowed - matching today's behaviour where every Investing/CNBC symbol in the
    /// old SierraMethods.SierraInstruments set was written.
    /// </summary>
    public string[] AllowedSymbols { get; set; } = Array.Empty<string>();
}

public sealed class CnbcOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMs { get; set; } = 1000;
    public string BaseUrl { get; set; } =
        "http://quote.cnbc.com/quote-html-webservice/quote.htm" +
        "?partnerId=2&requestMethod=quick&exthrs=1&noform=1&fund=1&output=json";
}

public sealed class InvestingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which transport to use when subscribing to investing.com.
    ///   "playwright" - launch a headless Chromium, load the jQuery stream bundle and scrape
    ///                  its console output. Matches what the old Selenium app did; works
    ///                  because the vendor's bundle handles its own handshake/origin/cookies.
    ///   "direct"    - open a raw Socket.IO connection to StreamUrl. Kept for later/testing;
    ///                 stream80.forexpros.com currently 404s every socket.io path tried.
    /// </summary>
    public string Transport { get; set; } = "playwright";

    /// <summary>
    /// Forexpros / investing.com socket.io endpoint used by the jQuery stream bundle.
    /// The value in the old CreateHTMLQuery was https://stream80.forexpros.com:443.
    /// Also embedded in the generated query.html so the bundle knows where to connect.
    /// </summary>
    public string StreamUrl { get; set; } = "https://stream80.forexpros.com:443";

    /// <summary>
    /// URLs of the vendor JS bundles loaded by the generated query.html. These are the
    /// exact versions the old app used; if investing.com retires them we'll need to bump.
    /// </summary>
    public string JQueryUrl { get; set; } = "https://i-invdn-com.akamaized.net/js/jquery-6.4.6.min.js";
    public string BundleUrl { get; set; } = "https://i-invdn-com.akamaized.net/js/main-1.17.55.min.js";

    /// <summary>
    /// Used as the page's origin when loading the query HTML through Playwright so the vendor
    /// bundle thinks it's running on investing.com (referer/origin are the most likely reason
    /// the raw WS endpoint refuses our requests).
    /// </summary>
    public string PageOrigin { get; set; } = "https://www.investing.com/";

    /// <summary>
    /// Reconnect backoff seed in milliseconds; doubled up to MaxReconnectMs.
    /// </summary>
    public int MinReconnectMs { get; set; } = 2000;
    public int MaxReconnectMs { get; set; } = 60_000;

    /// <summary>
    /// Engine.IO protocol version. Investing.com's bundle is very old, so EIO=3 is the starting
    /// assumption. Direct transport only.
    /// </summary>
    public int EngineIoVersion { get; set; } = 3;

    /// <summary>
    /// If the stream produces no ticks for this long, the Playwright client forces a page
    /// reload; the DirectSocketClient lets its reconnect loop handle it.
    /// </summary>
    public int SilentReloadSeconds { get; set; } = 180;

    /// <summary>Show the browser window for debugging. Default false (headless).</summary>
    public bool Headful { get; set; } = false;
}
