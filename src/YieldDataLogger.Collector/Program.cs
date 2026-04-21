using Microsoft.Extensions.Hosting;
using YieldDataLogger.Collector.DependencyInjection;
using YieldDataLogger.Collector.Instruments;

// Admin CLI: when the first arg is a known verb (list/show/add/remove/help), execute the
// command and exit without starting the worker host. Keeps instrument management one step
// away from editing JSON by hand until the Manager app lands.
if (AdminCli.IsAdminCommand(args))
{
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
builder.Services.AddYieldCollector(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
return 0;
