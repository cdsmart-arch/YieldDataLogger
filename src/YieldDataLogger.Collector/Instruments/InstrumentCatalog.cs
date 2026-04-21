using System.Text.Json;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Instruments;

/// <summary>
/// Loads the list of tracked instruments from instruments.json and exposes fast lookups
/// by Investing PID and by canonical symbol. Shape matches the old YieldLoggerUI JSON
/// ({ id, name, timestamp, lastPrice }) so the existing file can be reused as-is.
/// </summary>
public sealed class InstrumentCatalog
{
    private readonly Dictionary<int, Instrument> _byPid = new();
    private readonly Dictionary<string, Instrument> _bySymbol = new(StringComparer.Ordinal);
    private readonly ILogger<InstrumentCatalog> _logger;

    public IReadOnlyDictionary<int, Instrument> ByPid => _byPid;
    public IReadOnlyDictionary<string, Instrument> BySymbol => _bySymbol;
    public IReadOnlyCollection<Instrument> All => _bySymbol.Values;

    public InstrumentCatalog(ILogger<InstrumentCatalog> logger)
    {
        _logger = logger;
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Instrument catalog not found.", path);

        var json = File.ReadAllText(path);
        var dtos = JsonSerializer.Deserialize<List<InstrumentJsonDto>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new List<InstrumentJsonDto>();

        _byPid.Clear();
        _bySymbol.Clear();
        foreach (var dto in dtos)
        {
            var instr = new Instrument
            {
                InvestingPid = dto.Id > 0 ? dto.Id : null,
                CanonicalSymbol = dto.Name,
            };
            if (instr.InvestingPid is int pid)
                _byPid[pid] = instr;
            _bySymbol[instr.CanonicalSymbol] = instr;
        }

        _logger.LogInformation("Loaded {Count} instruments from {Path}", _bySymbol.Count, path);
    }

    private sealed class InstrumentJsonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public long Timestamp { get; set; }
        public double LastPrice { get; set; }
    }
}
