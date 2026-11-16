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

    public BybitConfig Bybit { get; init; } = new();

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

public sealed record BybitConfig
{
    public bool Enabled { get; init; } = true;

    /// <summary>Bybit linear (USDT perpetual) symbols. Each becomes _{SYMBOL}_BYBIT.scid.</summary>
    public string[] Symbols { get; init; } = ["XAUUSDT"];

    /// <summary>
    /// Days of trade history to load from Bybit's daily archive on first run. 0 = everything
    /// since the contract launched. Raising it later extends the file backwards.
    /// </summary>
    public int HistoryDays { get; init; } = 90;

    /// <summary>
    /// Sierra volumes are whole numbers; Bybit sizes are fractional (XAUUSDT: 0.001 oz steps).
    /// Volume = round(size × multiplier), so the default 1000 records volume in thousandths of an oz.
    /// </summary>
    public int VolumeMultiplier { get; init; } = 1000;
}
