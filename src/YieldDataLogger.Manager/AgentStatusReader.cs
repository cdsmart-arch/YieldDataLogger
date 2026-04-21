using System.IO;
using System.Text.Json;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Manager;

/// <summary>
/// Polls the Agent's status.json and produces a <see cref="ManagerState"/> that the
/// dashboard binds to. Deliberately tolerant: a missing file, a partial file, or a file
/// older than the stale threshold all become "Agent not running / stale" rather than
/// crashing the UI.
/// </summary>
internal sealed class AgentStatusReader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ManagerState Read(ManagerConfig cfg)
    {
        var path = cfg.StatusFilePath;

        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch (Exception ex) { return ManagerState.Missing(path, ex.Message); }

        if (!fi.Exists) return ManagerState.Missing(path, "status.json not found");

        AgentStatus? status;
        try
        {
            // FileShare.ReadWrite because the Agent rewrites atomically via move - reader
            // should never block nor fail even under concurrent replaces.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            status = JsonSerializer.Deserialize<AgentStatus>(fs, JsonOpts);
        }
        catch (Exception ex)
        {
            return ManagerState.Missing(path, "unreadable: " + ex.Message);
        }

        if (status is null) return ManagerState.Missing(path, "empty status file");

        var age = DateTime.UtcNow - status.WrittenAtUtc;
        var stale = age > cfg.StaleThreshold;

        return new ManagerState
        {
            StatusFilePath    = path,
            Status            = status,
            Stale             = stale,
            StaleReason       = stale ? $"last write {age.TotalSeconds:F0}s ago" : null,
        };
    }
}

/// <summary>UI-ready snapshot: status + "is it alive" assessment + the file we're watching.</summary>
internal sealed class ManagerState
{
    public string StatusFilePath { get; init; } = string.Empty;
    public AgentStatus? Status { get; init; }
    public bool Stale { get; init; }
    public string? StaleReason { get; init; }
    public string? ErrorMessage { get; init; }

    public bool AgentRunning => Status is not null && !Stale;

    public static ManagerState Missing(string path, string reason) => new()
    {
        StatusFilePath = path,
        Status         = null,
        Stale          = true,
        StaleReason    = reason,
        ErrorMessage   = reason,
    };
}
