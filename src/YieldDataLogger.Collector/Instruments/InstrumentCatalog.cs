using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Instruments;

/// <summary>
/// Unified instrument catalog. An instrument may have an Investing pid, a CNBC symbol, or both,
/// and is always keyed on CanonicalSymbol. Also supports the legacy YieldLoggerUI shape
/// (a flat array of { id, name, timestamp, lastPrice }) for back-compat.
/// Add/remove operations write the file atomically (temp-file + rename).
/// </summary>
public sealed class InstrumentCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Instrument> _byPid = new();
    private readonly Dictionary<string, Instrument> _byCnbc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Instrument> _bySymbol = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InstrumentCatalog> _logger;

    private string? _loadedFrom;

    public IReadOnlyDictionary<int, Instrument> ByPid
    {
        get { lock (_sync) return new Dictionary<int, Instrument>(_byPid); }
    }

    public IReadOnlyDictionary<string, Instrument> BySymbol
    {
        get { lock (_sync) return new Dictionary<string, Instrument>(_bySymbol, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyDictionary<string, Instrument> ByCnbc
    {
        get { lock (_sync) return new Dictionary<string, Instrument>(_byCnbc, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyCollection<Instrument> All
    {
        get { lock (_sync) return _bySymbol.Values.ToArray(); }
    }

    public IReadOnlyCollection<int> InvestingPids
    {
        get { lock (_sync) return _byPid.Keys.ToArray(); }
    }

    public IReadOnlyCollection<string> CnbcSymbols
    {
        get { lock (_sync) return _byCnbc.Keys.ToArray(); }
    }

    public string? SourcePath
    {
        get { lock (_sync) return _loadedFrom; }
    }

    public InstrumentCatalog(ILogger<InstrumentCatalog> logger)
    {
        _logger = logger;
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Instrument catalog not found.", path);

        var json = File.ReadAllText(path);
        var instruments = ParseCatalog(json);

        lock (_sync)
        {
            _byPid.Clear();
            _byCnbc.Clear();
            _bySymbol.Clear();
            foreach (var instr in instruments)
                Index(instr);
            _loadedFrom = path;
        }

        _logger.LogInformation(
            "Loaded {Count} instruments ({Pid} investing pids, {Cnbc} cnbc symbols) from {Path}",
            _bySymbol.Count, _byPid.Count, _byCnbc.Count, path);
    }

    /// <summary>
    /// Inserts or updates an instrument. Throws when the canonical symbol is already used by
    /// a different pid or cnbc mapping (prevents accidental collisions). Save() persists the
    /// change to disk.
    /// </summary>
    public void Upsert(Instrument instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument.CanonicalSymbol))
            throw new ArgumentException("CanonicalSymbol required", nameof(instrument));

        lock (_sync)
        {
            if (instrument.InvestingPid is int pid &&
                _byPid.TryGetValue(pid, out var byPid) &&
                !byPid.CanonicalSymbol.Equals(instrument.CanonicalSymbol, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Investing pid {pid} is already assigned to {byPid.CanonicalSymbol}.");
            }

            if (!string.IsNullOrEmpty(instrument.CnbcSymbol) &&
                _byCnbc.TryGetValue(instrument.CnbcSymbol, out var byCnbc) &&
                !byCnbc.CanonicalSymbol.Equals(instrument.CanonicalSymbol, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"CNBC symbol {instrument.CnbcSymbol} is already assigned to {byCnbc.CanonicalSymbol}.");
            }

            if (_bySymbol.TryGetValue(instrument.CanonicalSymbol, out var existing))
                Deindex(existing);
            Index(instrument);
        }
    }

    public bool Remove(string canonicalSymbol)
    {
        lock (_sync)
        {
            if (!_bySymbol.TryGetValue(canonicalSymbol, out var existing)) return false;
            Deindex(existing);
            return true;
        }
    }

    /// <summary>
    /// Persists the current catalog back to the file it was loaded from (or to <paramref name="path"/>
    /// when supplied) using a temp-file + rename so a crashed write can't leave a half-file.
    /// </summary>
    public void Save(string? path = null)
    {
        string target;
        InstrumentJsonDto[] toWrite;
        lock (_sync)
        {
            target = path ?? _loadedFrom
                ?? throw new InvalidOperationException("Catalog hasn't been loaded; call Load() first or pass a path.");
            toWrite = _bySymbol.Values
                .OrderBy(i => i.Category ?? "")
                .ThenBy(i => i.CanonicalSymbol, StringComparer.OrdinalIgnoreCase)
                .Select(InstrumentJsonDto.From)
                .ToArray();
        }

        var root = new CatalogFileDto
        {
            Version = 2,
            Instruments = toWrite,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        var tmp = target + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(root, options));
        if (File.Exists(target)) File.Replace(tmp, target, target + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, target);

        _logger.LogInformation("Saved {Count} instruments to {Path}", toWrite.Length, target);
    }

    private void Index(Instrument instr)
    {
        _bySymbol[instr.CanonicalSymbol] = instr;
        if (instr.InvestingPid is int pid) _byPid[pid] = instr;
        if (!string.IsNullOrEmpty(instr.CnbcSymbol)) _byCnbc[instr.CnbcSymbol] = instr;
    }

    private void Deindex(Instrument instr)
    {
        _bySymbol.Remove(instr.CanonicalSymbol);
        if (instr.InvestingPid is int pid) _byPid.Remove(pid);
        if (!string.IsNullOrEmpty(instr.CnbcSymbol)) _byCnbc.Remove(instr.CnbcSymbol);
    }

    private static IEnumerable<Instrument> ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // v2 shape: { "version": 2, "instruments": [ ... ] }
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("instruments", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
                yield return ReadV2(el);
            yield break;
        }

        // legacy shape: [ { id, name, timestamp, lastPrice }, ... ]
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
                yield return ReadLegacy(el);
            yield break;
        }

        throw new InvalidDataException(
            "Unrecognised instruments file shape. Expected either v2 { version, instruments:[...] } or legacy array of { id, name, ... }.");
    }

    private static Instrument ReadV2(JsonElement el)
    {
        var canonical = el.GetProperty("canonicalSymbol").GetString()
            ?? throw new InvalidDataException("canonicalSymbol is required on every instrument.");

        int? pid = null;
        if (el.TryGetProperty("investingPid", out var pidEl) &&
            pidEl.ValueKind is JsonValueKind.Number &&
            pidEl.TryGetInt32(out var p) && p > 0)
        {
            pid = p;
        }

        string? cnbc = null;
        if (el.TryGetProperty("cnbcSymbol", out var cnbcEl) &&
            cnbcEl.ValueKind == JsonValueKind.String)
        {
            var s = cnbcEl.GetString();
            if (!string.IsNullOrWhiteSpace(s)) cnbc = s;
        }

        string? category = null;
        if (el.TryGetProperty("category", out var catEl) &&
            catEl.ValueKind == JsonValueKind.String)
        {
            category = catEl.GetString();
        }

        return new Instrument
        {
            CanonicalSymbol = canonical,
            InvestingPid = pid,
            CnbcSymbol = cnbc,
            Category = category,
        };
    }

    private static Instrument ReadLegacy(JsonElement el)
    {
        var name = el.GetProperty("name").GetString()
            ?? throw new InvalidDataException("Legacy instrument missing 'name'.");
        int? pid = null;
        if (el.TryGetProperty("id", out var idEl) &&
            idEl.ValueKind is JsonValueKind.Number &&
            idEl.TryGetInt32(out var p) && p > 0)
        {
            pid = p;
        }
        return new Instrument
        {
            CanonicalSymbol = name,
            InvestingPid = pid,
        };
    }

    private sealed class CatalogFileDto
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("instruments")] public InstrumentJsonDto[] Instruments { get; set; } = Array.Empty<InstrumentJsonDto>();
    }

    private sealed class InstrumentJsonDto
    {
        [JsonPropertyName("canonicalSymbol")] public string CanonicalSymbol { get; set; } = "";
        [JsonPropertyName("investingPid")] public int? InvestingPid { get; set; }
        [JsonPropertyName("cnbcSymbol")] public string? CnbcSymbol { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }

        public static InstrumentJsonDto From(Instrument i) => new()
        {
            CanonicalSymbol = i.CanonicalSymbol,
            InvestingPid = i.InvestingPid,
            CnbcSymbol = i.CnbcSymbol,
            Category = i.Category,
        };
    }
}
