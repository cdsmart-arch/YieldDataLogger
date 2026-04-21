using System.IO;

namespace YieldDataLogger.Manager;

/// <summary>
/// Lightweight configuration resolved once at startup. The Manager deliberately has no JSON
/// config file yet - paths are derived from the same ProgramData conventions the Agent uses,
/// with environment overrides for development. Adding a proper settings UI is a later phase.
/// </summary>
internal sealed class ManagerConfig
{
    /// <summary>Absolute path to the Agent's status.json. Falls back to %TEMP% if the default does not exist.</summary>
    public string StatusFilePath { get; }

    /// <summary>
    /// Absolute path to the Agent's subscriptions.json. Always lives next to status.json, so
    /// it's derived from <see cref="StatusFilePath"/> rather than resolved independently.
    /// </summary>
    public string SubscriptionsFilePath =>
        Path.Combine(Path.GetDirectoryName(StatusFilePath) ?? string.Empty, "subscriptions.json");

    /// <summary>Refresh cadence for polling status + SQLite.</summary>
    public TimeSpan RefreshInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Agent is considered "stale" (and therefore not running) when status.json has not been
    /// updated within this window. Two intervals leaves room for disk stalls without flapping.
    /// </summary>
    public TimeSpan StaleThreshold { get; } = TimeSpan.FromSeconds(5);

    private ManagerConfig(string statusFilePath)
    {
        StatusFilePath = statusFilePath;
    }

    public static ManagerConfig Resolve()
    {
        // Primary location matches the Agent's default appsettings.
        var prod = Environment.ExpandEnvironmentVariables(@"%ProgramData%\YieldDataLogger\Agent\status.json");
        // Dev fallback matches the Agent's appsettings.Development override.
        var dev  = Environment.ExpandEnvironmentVariables(@"%TEMP%\YieldDataLogger\Agent\status.json");

        var envOverride = Environment.GetEnvironmentVariable("YIELDDATALOGGER_STATUS_PATH");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return new ManagerConfig(Environment.ExpandEnvironmentVariables(envOverride));

        // Prefer whichever file is currently fresh; otherwise fall back to the prod path
        // so the Manager still has a label to show while waiting for the Agent to start.
        if (IsFresh(dev) && !IsFresh(prod)) return new ManagerConfig(dev);
        return new ManagerConfig(File.Exists(prod) ? prod : (File.Exists(dev) ? dev : prod));
    }

    private static bool IsFresh(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc) < TimeSpan.FromSeconds(10);
        }
        catch { return false; }
    }
}
