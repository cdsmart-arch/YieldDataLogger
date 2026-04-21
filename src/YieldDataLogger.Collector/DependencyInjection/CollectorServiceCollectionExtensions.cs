using Microsoft.Extensions.Configuration;
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
using YieldDataLogger.Core.Models;
using YieldDataLogger.Core.Sinks;

namespace YieldDataLogger.Collector.DependencyInjection;

/// <summary>
/// Registers every piece of the collector pipeline (options, catalog, HTTP client, dispatcher,
/// sources, hosted services and the local Sqlite/Scid sinks) so both the standalone worker
/// (YieldDataLogger.Collector) and the web host (YieldDataLogger.Api) can share the exact
/// same plumbing. The API project disables the local sinks in its config and adds its own
/// SqlPriceSink / SignalRPriceSink on top.
/// </summary>
public static class CollectorServiceCollectionExtensions
{
    public static IServiceCollection AddYieldCollector(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CollectorOptions>(configuration.GetSection(CollectorOptions.SectionName));

        services.AddHttpClient("cnbc", http =>
        {
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("YieldDataLogger/1.0 (+collector)");
        });

        services.AddSingleton<InstrumentCatalog>();
        services.AddSingleton<TickDispatcher>();

        services.AddSingleton<IInvestingStreamClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CollectorOptions>>().Value.Investing;
            var transport = (opts.Transport ?? "playwright").Trim().ToLowerInvariant();
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("YieldDataLogger.Collector.Transport");
            switch (transport)
            {
                case "direct":
                    log.LogInformation("Investing transport: direct Socket.IO");
                    return ActivatorUtilities.CreateInstance<InvestingSocketClient>(sp);
                case "playwright":
                default:
                    if (transport != "playwright")
                        log.LogWarning("Unknown Investing transport '{T}', falling back to playwright", transport);
                    log.LogInformation("Investing transport: headless Playwright");
                    return ActivatorUtilities.CreateInstance<InvestingPlaywrightClient>(sp);
            }
        });

        services.AddSingleton<IPriceSink>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CollectorOptions>>().Value.Sinks.Sqlite;
            var logger = sp.GetRequiredService<ILogger<SqliteSink>>();
            if (!opts.Enabled)
            {
                logger.LogInformation("SqliteSink disabled");
                return new DisabledSink("sqlite");
            }
            var path = Environment.ExpandEnvironmentVariables(opts.Path);
            Directory.CreateDirectory(path);
            logger.LogInformation("SqliteSink path: {Path}", path);
            return new SqliteSink(path, logger);
        });

        services.AddSingleton<IPriceSink>(sp =>
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

        services.AddHostedService<InstrumentCatalogLoader>();
        services.AddHostedService<CnbcPoller>();
        services.AddHostedService<InvestingStreamHostedService>();

        ErrorLog.DefaultPath = Path.Combine(
            Environment.ExpandEnvironmentVariables(@"%ProgramData%\YieldDataLogger"),
            "errors.log");

        return services;
    }
}

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
        {
            // Look in the app's content root first (Collector layout: JSON sits next to the csproj
            // and the bin/Debug copy is what ships). Fall back to BaseDirectory for hosts like
            // the Api, which only receives the file as a <Link>ed copy-to-output item.
            var contentRoot = Path.Combine(_env.ContentRootPath, file);
            var baseDir = Path.Combine(AppContext.BaseDirectory, file);
            file = File.Exists(contentRoot) ? contentRoot : baseDir;
        }
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
    public ValueTask WriteAsync(PriceTick tick, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
