using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.ExchangeFeed.Bybit;

namespace YieldDataLogger.ExchangeFeed;

/// <summary>Writes status.json every 2 seconds (atomic tmp + rename) for diagnostics or a future UI.</summary>
public sealed class StatusWriter : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly FeedSupervisor _supervisor;
    private readonly ILogger<StatusWriter> _logger;
    private readonly string _path;
    private readonly DateTime _started = DateTime.UtcNow;

    public StatusWriter(FeedSupervisor supervisor, IOptions<ServiceOptions> options, ILogger<StatusWriter> logger)
    {
        _supervisor = supervisor;
        _logger = logger;
        _path = Environment.ExpandEnvironmentVariables(options.Value.StatusPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var warned = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = new
                {
                    WrittenAtUtc     = DateTime.UtcNow,
                    StartedAtUtc     = _started,
                    Pid              = Environment.ProcessId,
                    Version          = typeof(StatusWriter).Assembly.GetName().Version?.ToString(),
                    ConfigPath       = _supervisor.ConfigPath,
                    SierraDataFolder = _supervisor.Config?.SierraDataFolder,
                    Problem          = _supervisor.Problem,
                    Feeds            = _supervisor.Feeds.Select(Safe).ToArray(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(status, Json), stoppingToken);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!warned) _logger.LogWarning(ex, "Could not write {Path} (will keep trying)", _path);
                warned = true;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    // A snapshot races the feed task that owns the state; a torn read just skips one refresh.
    private static FeedStatus Safe(BybitSymbolFeed feed)
    {
        try { return feed.Snapshot(); }
        catch { return new FeedStatus { Exchange = "Bybit", File = feed.ScidPath, Phase = "(refreshing)" }; }
    }
}
