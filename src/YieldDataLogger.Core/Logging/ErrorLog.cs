namespace YieldDataLogger.Core.Logging;

/// <summary>
/// Append-only error log used by sinks and stream clients. Kept as a simple static helper
/// to mirror the old ErrorLog.AppendErrorLogFile utility; logging host infrastructure
/// (ILogger) is still used for normal operation - this is for "I tried to write SQLite
/// and it blew up, capture it somewhere durable" cases.
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static string DefaultPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "YieldDataLogger", "errors.log");

    public static void Append(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var line = $"{DateTime.UtcNow:O}  {message}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(DefaultPath, line);
            }
        }
        catch
        {
            // Error logger must never throw
        }
    }
}
