using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using YieldDataLogger.Collector.Configuration;
using YieldDataLogger.Collector.Instruments;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Collector.Sources;

/// <summary>
/// Headless-browser implementation of <see cref="IInvestingStreamClient"/>. Loads an HTML
/// page equivalent to the old YieldLoggerUI query.html into Chromium via Playwright,
/// letting the investing.com jQuery bundle do its own handshake/cookies/origin dance,
/// and scraping the bundle's console.log output to surface ticks.
///
/// Works for exactly the reason the original Selenium app worked: the vendor's own JS
/// manages the socket.io connection so we don't have to mimic any security headers.
///
/// The generated HTML is served in-response to a navigation to an investing.com URL so
/// that document origin = https://www.investing.com, which is what stream80.forexpros.com
/// now appears to require (the direct Socket.IO probe returns 404 without it).
///
/// First-time install (per machine; the NuGet package ships with only the .NET driver):
///   dotnet build
///   pwsh src/YieldDataLogger.Collector/bin/Debug/net8.0/playwright.ps1 install chromium
/// On Azure / Linux containers see Microsoft.Playwright.Program.Main-based alternatives.
/// </summary>
public sealed class InvestingPlaywrightClient : IInvestingStreamClient
{
    private readonly IOptionsMonitor<CollectorOptions> _options;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<InvestingPlaywrightClient> _logger;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private CancellationTokenSource? _reloadCts;
    private DateTime _lastTickUtc = DateTime.MinValue;
    private int _subscribedPidCount;
    private List<int> _currentPids = new();

    public event Func<PriceTick, CancellationToken, ValueTask>? OnTick;
    public bool IsLive => _page is not null && _subscribedPidCount > 0;
    public DateTime LastTickUtc => _lastTickUtc;

    public InvestingPlaywrightClient(
        IOptionsMonitor<CollectorOptions> options,
        InstrumentCatalog catalog,
        ILogger<InvestingPlaywrightClient> logger)
    {
        _options = options;
        _catalog = catalog;
        _logger = logger;
    }

    public async Task StartAsync(IEnumerable<int> pids, CancellationToken ct)
    {
        var opts = _options.CurrentValue.Investing;
        _currentPids = pids.Distinct().OrderBy(p => p).ToList();
        if (_currentPids.Count == 0)
        {
            _logger.LogWarning("InvestingPlaywrightClient.StartAsync called with no pids; nothing to subscribe.");
            return;
        }

        _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !opts.Headful,
            Args = new[] { "--disable-blink-features=AutomationControlled" },
        }).ConfigureAwait(false);

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36",
            Locale = "en-US",
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
        }).ConfigureAwait(false);

        _page = await _context.NewPageAsync().ConfigureAwait(false);
        _page.Console += OnPageConsole;
        _page.PageError += (_, err) => _logger.LogWarning("Investing page error: {Err}", err);
        _page.Crash += (_, _) => _logger.LogWarning("Investing page crashed");

        await InstallRouteAsync(_page, opts, _currentPids).ConfigureAwait(false);

        _subscribedPidCount = _currentPids.Count;
        _logger.LogInformation(
            "Investing Playwright launching (headful={Headful}) with {Count} pids via {Origin}",
            opts.Headful, _subscribedPidCount, opts.PageOrigin);

        await NavigateAsync(_page, opts, ct).ConfigureAwait(false);

        _reloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => WatchdogLoopAsync(_reloadCts.Token));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _reloadCts?.Cancel();
        try { if (_page is not null) await _page.CloseAsync().ConfigureAwait(false); } catch { }
        try { if (_context is not null) await _context.CloseAsync().ConfigureAwait(false); } catch { }
        try { if (_browser is not null) await _browser.CloseAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _playwright?.Dispose();
        _playwright = null;
    }

    private async Task NavigateAsync(IPage page, InvestingOptions opts, CancellationToken ct)
    {
        try
        {
            // Any investing.com URL works; our route handler below intercepts it and serves
            // the generated query.html. We navigate via goto so the document origin matches
            // the investing.com host and the vendor bundle's socket.io handshake is accepted.
            await page.GotoAsync(
                opts.PageOrigin.TrimEnd('/') + "/yield-data-logger",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000,
                }).ConfigureAwait(false);
            _logger.LogInformation("Investing page loaded at {Url}", page.Url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Investing page navigation failed; watchdog will retry");
        }
    }

    private async Task InstallRouteAsync(IPage page, InvestingOptions opts, IReadOnlyList<int> pids)
    {
        var originHost = new Uri(opts.PageOrigin).Host; // www.investing.com
        var html = BuildQueryHtml(opts, pids);

        await page.RouteAsync($"**/{originHost}/**", async route =>
        {
            // Serve our generated HTML for any navigation under the investing.com origin.
            // Everything else (CDN scripts, socket.io, etc.) continues untouched.
            if (route.Request.ResourceType == "document")
            {
                await route.FulfillAsync(new RouteFulfillOptions
                {
                    Status = 200,
                    ContentType = "text/html; charset=utf-8",
                    Body = html,
                }).ConfigureAwait(false);
            }
            else
            {
                await route.ContinueAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    private static string BuildQueryHtml(InvestingOptions opts, IReadOnlyList<int> pids)
    {
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < pids.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"').Append("pid-").Append(pids[i].ToString(CultureInfo.InvariantCulture)).Append(":\"");
        }
        sb.Append("]");
        var pidArray = sb.ToString();

        // Mirrors old YieldLoggerUI/CreateHTMLQuery.cs output. The vendor bundle listens for
        // a "socketRetry" jQuery event whose payload is an array of room names ("pid-NNN:"),
        // then raises "socketMessage" when a tick lands. We log every tick in a format our
        // OnPageConsole handler can parse with a simple comma split. Using $$""" so JS braces
        // stay single - interpolation uses {{ }}.
        return $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8" />
<title>YieldDataLogger stream</title>
<script type="text/javascript" src="{{opts.JQueryUrl}}"></script>
<script type="text/javascript" src="{{opts.BundleUrl}}"></script>
<script type="text/javascript">
  window.stream = "{{opts.StreamUrl}}";
  var TimeZoneID = 55;
  window.timezoneOffset = 0;
  window.uid = 0;

  $(function () {
    $(window).trigger("socketRetry", [{{pidArray}}]);

    $(window).on("socketMessage", function (evt, data) {
      try {
        console.log("entry_to_process," + data.timestamp + "," + data.pid + "," + data.last_numeric + ",");
      } catch (e) {
        console.error("yld parse error: " + e);
      }
    });
  });
</script>
</head>
<body>
<div id="yld-status">streaming {{pids.Count}} pids via {{opts.StreamUrl}}</div>
</body>
</html>
""";
    }

    private async void OnPageConsole(object? sender, IConsoleMessage msg)
    {
        try
        {
            var text = msg.Text;
            if (string.IsNullOrEmpty(text)) return;

            if (!text.StartsWith("entry_to_process,", StringComparison.Ordinal))
            {
                // Everything else (bundle noise, jquery logs, errors) at Debug so the first
                // runs show us what's happening without drowning the console in Info.
                if (msg.Type == "error" || msg.Type == "warning")
                    _logger.LogDebug("page[{Type}] {Text}", msg.Type, text);
                else
                    _logger.LogTrace("page[{Type}] {Text}", msg.Type, text);
                return;
            }

            // Format: "entry_to_process,<vendorTimestamp>,<pid>,<last_numeric>,"
            var parts = text.Split(',');
            if (parts.Length < 4) return;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                return;
            if (!double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                return;

            if (!_catalog.ByPid.TryGetValue(pid, out var instr)) return;

            _lastTickUtc = DateTime.UtcNow;
            var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tick = new PriceTick(instr.CanonicalSymbol, unix, price, "investing");

            var handler = OnTick;
            if (handler is null) return;

            // Playwright's Console event is raised on its own loop. async void here is the
            // normal pattern for event handlers - the try/catch below guarantees nothing
            // escapes unobserved.
            await handler(tick, CancellationToken.None).ConfigureAwait(false);

            _logger.LogDebug("Investing tick {Symbol} {Price}", instr.CanonicalSymbol, price);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OnPageConsole threw");
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var opts = _options.CurrentValue.Investing;
            var silentFor = _lastTickUtc == DateTime.MinValue
                ? TimeSpan.MaxValue
                : DateTime.UtcNow - _lastTickUtc;

            if (silentFor.TotalSeconds < opts.SilentReloadSeconds) continue;
            if (_page is null) continue;

            _logger.LogWarning(
                "Investing stream silent for {Seconds:F0}s, reloading Playwright page",
                silentFor == TimeSpan.MaxValue ? opts.SilentReloadSeconds : silentFor.TotalSeconds);

            try
            {
                await NavigateAsync(_page, opts, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Investing page reload failed");
            }
        }
    }
}
