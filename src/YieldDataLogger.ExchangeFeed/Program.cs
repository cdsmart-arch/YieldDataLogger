using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YieldDataLogger.Core.Logging;
using YieldDataLogger.ExchangeFeed;

// CSI Exchange Feed: a standalone Windows service that streams exchange trades (Bybit today)
// straight into Sierra Chart .scid files with real volume and aggressor side. Independent of
// the YieldDataLogger Agent - own service, own config (ProgramData\...\ExchangeFeed\config.json),
// own status file, own installer. Shares only the .scid writer in YieldDataLogger.Core.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(o => o.ServiceName = "YieldDataLogger.ExchangeFeed");
builder.Services.Configure<ServiceOptions>(builder.Configuration.GetSection(ServiceOptions.SectionName));

builder.Services.AddHttpClient("bybit", http =>
{
    http.Timeout = TimeSpan.FromMinutes(2);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CSI-ExchangeFeed/1.0");
});

builder.Services.AddSingleton<FeedSupervisor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FeedSupervisor>());
builder.Services.AddHostedService<StatusWriter>();

ErrorLog.DefaultPath = Path.Combine(
    Environment.ExpandEnvironmentVariables(@"%ProgramData%\YieldDataLogger\ExchangeFeed"),
    "errors.log");

await builder.Build().RunAsync();
