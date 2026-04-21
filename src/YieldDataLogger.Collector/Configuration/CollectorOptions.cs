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
    /// Forexpros / investing.com socket.io endpoint used by the jQuery stream bundle.
    /// The value in the old CreateHTMLQuery was https://stream80.forexpros.com:443.
    /// </summary>
    public string StreamUrl { get; set; } = "https://stream80.forexpros.com:443";

    /// <summary>
    /// Reconnect backoff seed in milliseconds; doubled up to MaxReconnectMs.
    /// </summary>
    public int MinReconnectMs { get; set; } = 2000;
    public int MaxReconnectMs { get; set; } = 60_000;

    /// <summary>
    /// Engine.IO protocol version. Investing.com's bundle is very old, so EIO=3 is the starting
    /// assumption. If direct connect keeps failing the Playwright fallback will be used.
    /// </summary>
    public int EngineIoVersion { get; set; } = 3;
}
