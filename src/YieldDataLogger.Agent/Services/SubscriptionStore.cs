using System.Text.Json;
using Microsoft.Extensions.Logging;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Agent.Services;

/// <summary>
/// File-backed persistence for the Agent's subscription list. Deliberately kept trivial:
/// the Manager writes the file, the Agent reads/watches it, and both sides use the same
/// <see cref="AgentSubscriptions"/> DTO so the contract is unambiguous.
///
/// Writes go via a temp file + rename so the Agent's FileSystemWatcher never sees a
/// half-written document; reads tolerate the usual "IO exception while being written"
/// race by returning <c>null</c> and letting the caller retry.
/// </summary>
public sealed class SubscriptionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILogger<SubscriptionStore> _logger;
    public string FilePath { get; }

    public SubscriptionStore(string filePath, ILogger<SubscriptionStore> logger)
    {
        FilePath = filePath;
        _logger = logger;
    }

    public AgentSubscriptions? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<AgentSubscriptions>(fs, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load subscriptions from {Path}; treating as absent.", FilePath);
            return null;
        }
    }

    public void Save(IEnumerable<string> symbols)
    {
        var snapshot = new AgentSubscriptions
        {
            Symbols      = symbols.ToArray(),
            UpdatedAtUtc = DateTime.UtcNow,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            JsonSerializer.Serialize(fs, snapshot, JsonOptions);
        // Atomic swap: the watcher will see exactly one "changed" event.
        File.Move(tmp, FilePath, overwrite: true);
    }
}
