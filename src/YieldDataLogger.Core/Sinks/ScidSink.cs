using System.Text;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Core.Sinks;

/// <summary>
/// Appends ticks to Sierra Chart intraday (.scid) files under the configured Sierra data folder.
/// Only the symbols in <see cref="AllowedSymbols"/> are written to - mirrors the explicit
/// subscription list that lived in the old SierraMethods.SierraInstruments HashSet.
/// Binary layout is exactly what Sierra Chart expects: 56-byte header, 40-byte records.
/// </summary>
public sealed class ScidSink : IPriceSink
{
    private readonly string _sierraDataFolder;
    private readonly HashSet<string> _allowed;
    private readonly ILogger<ScidSink> _logger;
    private readonly Dictionary<string, double> _lastPriceBySymbol = new(StringComparer.Ordinal);
    private readonly object _dedupGate = new();

    public string Name => "scid";

    /// <summary>Symbols this sink is permitted to write. Empty set = write nothing.</summary>
    public IReadOnlyCollection<string> AllowedSymbols => _allowed;

    public ScidSink(string sierraDataFolder, IEnumerable<string> allowedSymbols, ILogger<ScidSink> logger)
    {
        _sierraDataFolder = sierraDataFolder ?? throw new ArgumentNullException(nameof(sierraDataFolder));
        _allowed = new HashSet<string>(allowedSymbols, StringComparer.Ordinal);
        _logger = logger;
    }

    public ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
    {
        if (!_allowed.Contains(tick.CanonicalSymbol))
            return ValueTask.CompletedTask;

        lock (_dedupGate)
        {
            if (_lastPriceBySymbol.TryGetValue(tick.CanonicalSymbol, out var last) &&
                last == tick.Price)
            {
                return ValueTask.CompletedTask;
            }
            _lastPriceBySymbol[tick.CanonicalSymbol] = tick.Price;
        }

        var file = Path.Combine(_sierraDataFolder, $"_{tick.CanonicalSymbol}.scid");
        try
        {
            WriteHeaderIfMissing(file);
            WriteRecord(file, tick.Price);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScidSink write failed for {Symbol}", tick.CanonicalSymbol);
            ErrorLog.Append($"ScidSink write failed for {tick.CanonicalSymbol}: {ex}");
        }

        return ValueTask.CompletedTask;
    }

    private static void WriteHeaderIfMissing(string sierraFileName)
    {
        var dir = Path.GetDirectoryName(sierraFileName);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(sierraFileName))
            return;

        using var stream = File.Open(sierraFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
        writer.Write(new[] { 'S', 'C', 'I', 'D' });
        writer.Write((uint)56);   // HeaderSize
        writer.Write((uint)40);   // RecordSize
        writer.Write((ushort)1);  // Version
        writer.Write((ushort)0);  // Unused
        writer.Write((uint)0);    // UTCStartIndex
        writer.Write(new char[36]); // Reserve
    }

    private static void WriteRecord(string sierraFileName, double price)
    {
        if (!File.Exists(sierraFileName))
            return;

        using var stream = File.Open(sierraFileName, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, false);
        writer.Write(DateTimeToSierraTimestamp(DateTime.UtcNow));
        writer.Write((float)price); // open
        writer.Write((float)price); // high
        writer.Write((float)price); // low
        writer.Write((float)price); // close
        writer.Write((uint)0);      // NumTrades
        writer.Write((uint)0);      // TotalVolume
        writer.Write((uint)0);      // BidVolume
        writer.Write((uint)0);      // AskVolume
    }

    private static long DateTimeToSierraTimestamp(DateTime dt)
    {
        // Sierra Chart timestamps are microseconds since 1899-12-30 UTC
        var baseDate = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc);
        return Convert.ToInt64((dt - baseDate).TotalSeconds) * 1_000_000;
    }
}
