using System.Text.Json;
using System.Text.Json.Serialization;

namespace YieldDataLogger.ExchangeFeed;

/// <summary>Fixed service paths, from appsettings.json "ExchangeFeed". %ENV% variables are expanded.</summary>
public sealed class ServiceOptions
{
    public const string SectionName = "ExchangeFeed";

    /// <summary>User-editable settings (Sierra folder, symbols, history). Lives in ProgramData so upgrades keep it.</summary>
    public string ConfigPath { get; set; } = "%ProgramData%\\YieldDataLogger\\ExchangeFeed\\config.json";

    /// <summary>Status snapshot rewritten every couple of seconds.</summary>
    public string StatusPath { get; set; } = "%ProgramData%\\YieldDataLogger\\ExchangeFeed\\status.json";

    /// <summary>Per-symbol state files and the archive download cache.</summary>
    public string DataPath { get; set; } = "%ProgramData%\\YieldDataLogger\\ExchangeFeed\\data";
}

/// <summary>
/// config.json - edited by the user (or written once by the installer). The service re-reads
/// it every 15 seconds and restarts the feeds when anything changes, so no service restart
/// is needed after an edit.
/// </summary>
public sealed record FeedConfig
{
    /// <summary>Sierra Chart Data folder the .scid files are written into.</summary>
    public string SierraDataFolder { get; init; } = "C:\\SierraChart\\Data";

    /// <summary>Bybit linear perpetuals. XAUUSDT matches the TradingView series the sun levels are built on.</summary>
    public ExchangeConfig Bybit { get; init; } = new()
    {
        Symbols     = ["XAUUSDT"],
        HistoryDays = 90,
    };

    /// <summary>Binance USDⓈ-M perpetuals (aggTrades). XAUUSDT is ~17x Bybit's volume; COPPERUSDT is $/lb like HG.</summary>
    public ExchangeConfig Binance { get; init; } = new()
    {
        Symbols             = ["XAUUSDT", "COPPERUSDT"],
        HistoryDays         = 30,
        HistoryDaysBySymbol = new() { ["COPPERUSDT"] = 90 },
    };

    /// <summary>Binance spot (aggTrades), e.g. EURUSDT. Off unless symbols are listed. Files: _{SYMBOL}_BINANCESPOT.scid.</summary>
    public ExchangeConfig BinanceSpot { get; init; } = new()
    {
        Symbols     = [],
        HistoryDays = 30,
    };

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.Never,
    };

    /// <summary>Loads the config, writing a default file first if there is none.</summary>
    public static FeedConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var dflt = new FeedConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dflt, Json));
            return dflt;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<FeedConfig>(fs, Json) ?? new FeedConfig();
    }
}

/// <summary>One exchange's feeds. Each symbol becomes _{SYMBOL}_{EXCHANGE}.scid.</summary>
public sealed record ExchangeConfig
{
    public bool Enabled { get; init; } = true;

    public string[] Symbols { get; init; } = [];

    /// <summary>
    /// Days of trade history to load from the exchange's daily archive on first run. 0 = everything
    /// since the contract launched. Raising it later extends the file backwards.
    /// </summary>
    public int HistoryDays { get; init; } = 30;

    /// <summary>Per-symbol <see cref="HistoryDays"/> overrides, e.g. { "COPPERUSDT": 90 }.</summary>
    public Dictionary<string, int>? HistoryDaysBySymbol { get; init; }

    /// <summary>
    /// Sierra volumes are whole numbers; exchange sizes are fractional. Volume = round(size × multiplier).
    /// 0 = automatic: 1 / the contract's quantity step (XAUUSDT 0.001 -> 1000, COPPERUSDT 0.1 -> 10).
    /// Changing it for a symbol rebuilds that symbol's file.
    /// </summary>
    public int VolumeMultiplier { get; init; }

    /// <summary>Per-symbol <see cref="VolumeMultiplier"/> overrides.</summary>
    public Dictionary<string, int>? VolumeMultipliers { get; init; }

    public IEnumerable<string> NormalizedSymbols() =>
        Symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct();

    public int HistoryDaysFor(string symbol) => Lookup(HistoryDaysBySymbol, symbol) ?? HistoryDays;

    /// <summary>null = automatic.</summary>
    public int? VolumeMultiplierFor(string symbol) =>
        Lookup(VolumeMultipliers, symbol) is { } v and > 0 ? v : VolumeMultiplier > 0 ? VolumeMultiplier : null;

    private static int? Lookup(Dictionary<string, int>? map, string symbol) =>
        map?.FirstOrDefault(kv => string.Equals(kv.Key, symbol, StringComparison.OrdinalIgnoreCase)) is { Key: not null } kv
            ? kv.Value : null;
}
