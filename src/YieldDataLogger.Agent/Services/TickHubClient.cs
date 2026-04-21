using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YieldDataLogger.Agent.Configuration;
using YieldDataLogger.Collector.Pipeline;
using YieldDataLogger.Core.Models;

namespace YieldDataLogger.Agent.Services;

/// <summary>
/// Connects to the SignalR hub published by the Api/Collector, subscribes to the configured
/// symbol list, and pushes each incoming tick into the local <see cref="TickDispatcher"/> so
/// registered sinks (SqliteSink, ScidSink, ...) persist it.
///
/// Automatic reconnect is provided by the SignalR client; on each reconnect we re-apply the
/// subscription list so the server reattaches us to the right groups.
/// </summary>
public sealed class TickHubClient : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly TickDispatcher _dispatcher;
    private readonly ILogger<TickHubClient> _logger;
    private HubConnection? _connection;
    private long _ticksReceived;

    public TickHubClient(
        IOptions<AgentOptions> options,
        TickDispatcher dispatcher,
        ILogger<TickHubClient> logger)
    {
        _options = options.Value;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.HubUrl))
        {
            _logger.LogError("Agent:HubUrl is empty - cannot start hub client.");
            return;
        }

        var normalisedSymbols = (_options.Symbols ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _logger.LogInformation(
            "Agent connecting to {Url} ; subscribing to {Count} symbols: {Symbols}",
            _options.HubUrl, normalisedSymbols.Length, string.Join(", ", normalisedSymbols));

        _connection = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl, opts =>
            {
                if (!string.IsNullOrWhiteSpace(_options.AuthToken))
                {
                    var token = _options.AuthToken!;
                    opts.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<TickPayload>("Tick", OnTick);

        _connection.Reconnecting += ex =>
        {
            _logger.LogWarning(ex, "Hub connection reconnecting...");
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            _logger.LogInformation("Hub reconnected - re-applying subscriptions.");
            if (normalisedSymbols.Length > 0)
            {
                try { await _connection.InvokeAsync("Subscribe", normalisedSymbols, stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Re-subscribe after reconnect failed"); }
            }
        };
        _connection.Closed += ex =>
        {
            _logger.LogWarning(ex, "Hub connection closed.");
            return Task.CompletedTask;
        };

        // Outer retry loop: if even the first connect fails (server not up yet) we keep trying.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _connection.StartAsync(stoppingToken);
                _logger.LogInformation("Hub connected ({ConnectionId}).", _connection.ConnectionId);

                if (normalisedSymbols.Length > 0)
                {
                    await _connection.InvokeAsync("Subscribe", normalisedSymbols, stoppingToken);
                    _logger.LogInformation("Subscribed to {Count} symbols.", normalisedSymbols.Length);
                }
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                var delay = Math.Max(1, _options.ReconnectDelaySeconds);
                _logger.LogWarning(ex, "Hub connect failed; retrying in {Delay}s...", delay);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Wait until the host shuts us down.
        var tcs = new TaskCompletionSource();
        using var reg = stoppingToken.Register(() => tcs.TrySetResult());
        await tcs.Task;

        try { await _connection.StopAsync(CancellationToken.None); }
        catch { /* shutdown */ }
        await _connection.DisposeAsync();
    }

    private async Task OnTick(TickPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Symbol)) return;

        Interlocked.Increment(ref _ticksReceived);
        var tick = new PriceTick(
            CanonicalSymbol: payload.Symbol,
            UnixTimeSeconds: payload.TsUnix,
            Price: payload.Price,
            Source: payload.Source ?? "hub");

        try
        {
            await _dispatcher.DispatchAsync(tick).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dispatch failed for {Symbol}", payload.Symbol);
        }
    }

    /// <summary>Payload shape emitted by the server's SignalRPriceSink ("Tick" message).</summary>
    private sealed record TickPayload(string Symbol, double TsUnix, double Price, string Source);
}
