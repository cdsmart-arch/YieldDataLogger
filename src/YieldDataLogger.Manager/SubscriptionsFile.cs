using System.IO;
using System.Text.Json;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Manager;

/// <summary>
/// Manager-side read/write of the same <c>subscriptions.json</c> the Agent consumes. Kept
/// separate from the Agent's <c>SubscriptionStore</c> to avoid dragging the Agent project
/// as a Manager reference, and to keep the Manager side's error handling UI-friendly
/// (exceptions surface to the window rather than a logger).
/// </summary>
internal static class SubscriptionsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static AgentSubscriptions? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<AgentSubscriptions>(fs, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Atomic write via tmp + rename so the Agent's FileSystemWatcher never sees a partial file.</summary>
    public static void Save(string path, IEnumerable<string> symbols, int historyDays = 0, int backfillDelayMs = 0)
    {
        var dto = new AgentSubscriptions
        {
            Symbols         = symbols.ToArray(),
            HistoryDays     = historyDays,
            BackfillDelayMs = backfillDelayMs,
            UpdatedAtUtc    = DateTime.UtcNow,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            JsonSerializer.Serialize(fs, dto, JsonOptions);
        File.Move(tmp, path, overwrite: true);
    }
}
