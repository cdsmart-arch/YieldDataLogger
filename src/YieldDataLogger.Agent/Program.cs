using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Agent.Configuration;
using YieldDataLogger.Agent.Services;
using YieldDataLogger.Collector.Pipeline;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Sinks;

// Pin the content root to the exe's directory so appsettings.json is found regardless of
// where the user launches us from (service control manager, scheduled task, explorer, ...).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Registers the Windows Service lifetime so the process responds to SCM start/stop signals.
// No-op when running interactively (dotnet run, double-click), so the same binary works for
// both development and the installed Windows Service.
builder.Services.AddWindowsService(opts => opts.ServiceName = "YieldDataLogger.Agent");

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));

builder.Services.AddSingleton<TickDispatcher>();

// Sinks: reuse the existing Core implementations. Factory registration keeps the construction
// dependent on options values (Enabled flag, path resolution) without spreading that logic
// across types. Disabled sinks are simply not registered, so the dispatcher never sees them.
builder.Services.AddSingleton<IPriceSink>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Sinks.Sqlite;
    var logger = sp.GetRequiredService<ILogger<SqliteSink>>();
    if (!opts.Enabled)
    {
        logger.LogInformation("SqliteSink disabled");
        return new NoopSink("sqlite(disabled)");
    }
    var path = Environment.ExpandEnvironmentVariables(opts.Path);
    Directory.CreateDirectory(path);
    logger.LogInformation("SqliteSink path: {Path}", path);
    return new SqliteSink(path, logger);
});

builder.Services.AddSingleton<IPriceSink>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Sinks.Scid;
    var logger = sp.GetRequiredService<ILogger<ScidSink>>();
    if (!opts.Enabled)
    {
        logger.LogInformation("ScidSink disabled");
        return new NoopSink("scid(disabled)");
    }
    var path = Environment.ExpandEnvironmentVariables(opts.Path);
    Directory.CreateDirectory(path);
    logger.LogInformation("ScidSink path: {Path} allowed={Allowed}", path,
        opts.AllowedSymbols.Length == 0 ? "(none)" : string.Join(",", opts.AllowedSymbols));
    return new ScidSink(path, opts.AllowedSymbols, logger);
});

// Subscription plumbing: store (file IO) -> manager (in-memory + events) -> watcher (tail the
// file, push changes through the manager). Order matters for construction only - the manager
// loads the current set in its constructor, so by the time the hub client runs it already has
// an authoritative list.
builder.Services.AddSingleton<SubscriptionStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    var path = Environment.ExpandEnvironmentVariables(opts.SubscriptionsPath);
    return new SubscriptionStore(path, sp.GetRequiredService<ILogger<SubscriptionStore>>());
});
builder.Services.AddSingleton<SubscriptionManager>();
builder.Services.AddHostedService<SubscriptionWatcherService>();

// Register TickHubClient once as singleton + hosted service so the StatusWriter can inject it
// to read live connection state without running a second copy.
builder.Services.AddSingleton<TickHubClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TickHubClient>());

// BackfillTracker: shared between HistoryBackfillService (writes progress) and
// StatusWriterService (reads it into the status.json the Manager polls).
builder.Services.AddSingleton<BackfillTracker>();

builder.Services.AddHostedService<StatusWriterService>();

// HttpClient used by the history backfill service.
builder.Services.AddHttpClient("backfill");

// History backfill: runs after each hub connect, pulls missing ticks from the REST API,
// and bulk-inserts them into the local SQLite files so NinjaTrader always has full history.
builder.Services.AddHostedService<HistoryBackfillService>();

// Route durable error logging to the same ProgramData tree the other processes use.
ErrorLog.DefaultPath = Path.Combine(
    Environment.ExpandEnvironmentVariables(@"%ProgramData%\YieldDataLogger"),
    "errors.log");

var host = builder.Build();
await host.RunAsync();

internal sealed class NoopSink : IPriceSink
{
    public string Name { get; }
    public NoopSink(string name) { Name = name; }
    public ValueTask WriteAsync(YieldDataLogger.Core.Models.PriceTick tick, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
