using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Collector.Configuration;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Collector.Pipeline;
using YieldDataLogger.Collector.Sources;
using YieldDataLogger.Core.Abstractions;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.Core.Sinks;

// Admin CLI: when the first arg is a known verb (list/show/add/remove/help), execute the
// command and exit without starting the worker host. Keeps instrument management one step
// away from editing JSON by hand until the Manager app lands.
if (AdminCli.IsAdminCommand(args))
{
    // Allow --file <path> to target a specific instruments.json (useful in dev where
    // we want edits to persist in the source project rather than the bin copy).
    var file = ExtractFileArg(args) ?? Path.Combine(AppContext.BaseDirectory, "instruments.json");
    return AdminCli.Run(args, file);
}

static string? ExtractFileArg(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--file", StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<CollectorOptions>(
    builder.Configuration.GetSection(CollectorOptions.SectionName));

builder.Services.AddHttpClient("cnbc", http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("YieldDataLogger/1.0 (+collector)");
});

builder.Services.AddSingleton<InstrumentCatalog>();
builder.Services.AddSingleton<TickDispatcher>();
builder.Services.AddSingleton<IInvestingStreamClient, InvestingSocketClient>();

// Sinks are registered as IPriceSink; the dispatcher picks them all up via IEnumerable<IPriceSink>.
builder.Services.AddSingleton<IPriceSink>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CollectorOptions>>().Value.Sinks.Sqlite;
    var logger = sp.GetRequiredService<ILogger<SqliteSink>>();
    var path = Environment.ExpandEnvironmentVariables(opts.Path);
    Directory.CreateDirectory(path);
    logger.LogInformation("SqliteSink path: {Path} enabled={Enabled}", path, opts.Enabled);
    // Use a no-op wrapper when disabled so DI still resolves a single sink; keeps wiring simple.
    return opts.Enabled ? new SqliteSink(path, logger) : new DisabledSink("sqlite");
});

builder.Services.AddSingleton<IPriceSink>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<CollectorOptions>>().Value.Sinks.Scid;
    var logger = sp.GetRequiredService<ILogger<ScidSink>>();
    if (!opts.Enabled) return new DisabledSink("scid");
    var path = Environment.ExpandEnvironmentVariables(opts.Path);
    Directory.CreateDirectory(path);
    logger.LogInformation("ScidSink path: {Path} allowed={Allowed}", path,
        opts.AllowedSymbols.Length == 0 ? "(all)" : string.Join(",", opts.AllowedSymbols));
    return new ScidSink(path, opts.AllowedSymbols, logger);
});

builder.Services.AddHostedService<InstrumentCatalogLoader>();
builder.Services.AddHostedService<CnbcPoller>();
builder.Services.AddHostedService<InvestingStreamHostedService>();

// Route ErrorLog's durable file into the same root the sinks are writing into.
ErrorLog.DefaultPath = Path.Combine(
    Environment.ExpandEnvironmentVariables(@"%ProgramData%\YieldDataLogger"),
    "errors.log");

var host = builder.Build();
await host.RunAsync();
return 0;


/// <summary>
/// Loads instruments.json once at host startup. Registered as a hosted service so it runs
/// before the sources, which depend on the catalog being populated.
/// </summary>
internal sealed class InstrumentCatalogLoader : IHostedService
{
    private readonly InstrumentCatalog _catalog;
    private readonly IOptions<CollectorOptions> _options;
    private readonly IHostEnvironment _env;

    public InstrumentCatalogLoader(InstrumentCatalog catalog, IOptions<CollectorOptions> options, IHostEnvironment env)
    {
        _catalog = catalog;
        _options = options;
        _env = env;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var file = _options.Value.InstrumentsFile;
        if (!Path.IsPathRooted(file))
            file = Path.Combine(_env.ContentRootPath, file);
        _catalog.Load(file);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Placeholder sink used when a concrete sink is disabled via config.</summary>
internal sealed class DisabledSink : IPriceSink
{
    public string Name { get; }
    public DisabledSink(string name) { Name = name + "(disabled)"; }
    public ValueTask WriteAsync(YieldDataLogger.Core.Models.PriceTick tick, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
