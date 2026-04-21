using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using YieldDataLogger.Collector.Configuration;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Collector.Pipeline;
using YieldDataLogger.Core;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Sources;

/// <summary>
/// Polls CNBC's quick-quote HTML web service (the same endpoint the old HTMLClientProcessor
/// used) on a timer and dispatches each returned symbol/last pair as a <see cref="PriceTick"/>.
/// Pure HTTP - no browser, no Selenium.
/// </summary>
public sealed class CnbcPoller : BackgroundService
{
    private readonly IOptionsMonitor<CollectorOptions> _options;
    private readonly TickDispatcher _dispatcher;
    private readonly IHttpClientFactory _httpFactory;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<CnbcPoller> _logger;

    public CnbcPoller(
        IOptionsMonitor<CollectorOptions> options,
        TickDispatcher dispatcher,
        IHttpClientFactory httpFactory,
        InstrumentCatalog catalog,
        ILogger<CnbcPoller> logger)
    {
        _options = options;
        _dispatcher = dispatcher;
        _httpFactory = httpFactory;
        _catalog = catalog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Cnbc.Enabled)
        {
            _logger.LogInformation("CNBC poller disabled via config, exiting.");
            return;
        }

        var opts = _options.CurrentValue.Cnbc;
        var symbols = _catalog.CnbcSymbols.ToArray();
        if (symbols.Length == 0)
        {
            _logger.LogWarning("CNBC poller enabled but no instruments in the catalog carry a cnbcSymbol.");
            return;
        }

        var requestUrl = $"{opts.BaseUrl}&symbols={string.Join("|", symbols)}";
        _logger.LogInformation("CNBC poller started: {SymbolCount} symbols every {IntervalMs} ms",
            symbols.Length, opts.PollIntervalMs);

        var http = _httpFactory.CreateClient("cnbc");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(http, requestUrl, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CNBC poll iteration failed");
            }
            try { await Task.Delay(opts.PollIntervalMs, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task PollOnceAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var items = JObject.Parse(body)["QuickQuoteResult"]?["QuickQuote"];
        if (items is null) return;

        foreach (var item in items)
        {
            var symbol = item["symbol"]?.ToString();
            var priceStr = item["last"]?.ToString();
            if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(priceStr))
                continue;

            if (!double.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var price))
                continue;

            var canonical = SymbolTranslator.FromCnbc(symbol);
            var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await _dispatcher.DispatchAsync(
                new PriceTick(canonical, unix, price, "cnbc"), ct).ConfigureAwait(false);
        }
    }
}
