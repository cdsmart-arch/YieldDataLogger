using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Agent.Configuration;
using YieldDataLogger.Collector.Pipeline;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Agent.Services;

/// <summary>
/// Periodically writes a JSON snapshot of the Agent's state to a well-known path so the
/// Manager dashboard (and any other diagnostic tooling) can render a live view without
/// needing sockets, pipes, or shared memory. Writing is atomic via tmp+rename so readers
/// never see a partial file.
/// </summary>
public sealed class StatusWriterService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AgentOptions _options;
    private readonly TickHubClient _hub;
    private readonly TickDispatcher _dispatcher;
    private readonly ILogger<StatusWriterService> _logger;
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly int _pid = Environment.ProcessId;

    public StatusWriterService(
        IOptions<AgentOptions> options,
        TickHubClient hub,
        TickDispatcher dispatcher,
        ILogger<StatusWriterService> logger)
    {
        _options = options.Value;
        _hub = hub;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = _options.Status;
        if (!cfg.Enabled)
        {
            _logger.LogInformation("StatusWriter disabled");
            return;
        }

        var path = Environment.ExpandEnvironmentVariables(cfg.Path);
        var dir  = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        _logger.LogInformation("StatusWriter path: {Path} (every {Seconds}s)", path, cfg.IntervalSeconds);

        // Resolved sink paths for display only - the Manager uses these to locate SQLite files.
        var sqliteSinkPath = _options.Sinks.Sqlite.Enabled
            ? Environment.ExpandEnvironmentVariables(_options.Sinks.Sqlite.Path)
            : string.Empty;
        var scidSinkPath = _options.Sinks.Scid.Enabled
            ? Environment.ExpandEnvironmentVariables(_options.Sinks.Scid.Path)
            : string.Empty;

        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.IntervalSeconds));

        // API base URL: explicit config wins; otherwise derive it from HubUrl by stripping
        // the conventional "/hubs/..." suffix. Lets the Manager find the REST catalogue
        // endpoint with zero extra configuration in the common case.
        var apiBase = !string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
            ? _options.ApiBaseUrl!.TrimEnd('/')
            : DeriveApiBase(_options.HubUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = new AgentStatus
                {
                    WrittenAtUtc      = DateTime.UtcNow,
                    StartedAtUtc      = _startedAtUtc,
                    Pid               = _pid,
                    HubUrl            = _hub.HubUrl,
                    ApiBaseUrl        = apiBase,
                    ConnectionState   = _hub.ConnectionState.ToString(),
                    ConnectionId      = _hub.ConnectionId,
                    SubscribedSymbols = _hub.SubscribedSymbols,
                    LastTickUtc       = _hub.LastTickUtc,
                    LastTickSymbol    = _hub.LastTickSymbol,
                    TicksReceived     = _hub.TicksReceived,
                    TicksDeduped      = _dispatcher.TicksDeduped,
                    TicksDispatched   = _dispatcher.TicksDispatched,
                    SqliteSinkPath    = sqliteSinkPath,
                    ScidSinkPath      = scidSinkPath,
                    LastError         = _hub.LastError,
                };

                await WriteAtomicAsync(path, snapshot, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write status file {Path}", path);
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string DeriveApiBase(string hubUrl)
    {
        if (string.IsNullOrWhiteSpace(hubUrl)) return string.Empty;
        if (!Uri.TryCreate(hubUrl, UriKind.Absolute, out var uri)) return string.Empty;
        // Strip the path entirely - the REST endpoints live under /api, not /hubs.
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static async Task WriteAtomicAsync(string path, AgentStatus status, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(fs, status, JsonOpts, ct);
        }
        // File.Move with overwrite is atomic on NTFS, so readers never observe a half-written file.
        File.Move(tmp, path, overwrite: true);
    }
}
