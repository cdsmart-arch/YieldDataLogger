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
///
/// Exposes public read-only properties consumed by <c>StatusWriterService</c> so the Manager
/// dashboard can observe connection state and the most recently received tick without any
/// cross-process IPC.
/// </summary>
public sealed class TickHubClient : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly TickDispatcher _dispatcher;
    private readonly SubscriptionManager _subscriptions;
    private readonly ILogger<TickHubClient> _logger;
    private HubConnection? _connection;
    private long _ticksReceived;
    private long _lastTickTicks;     // DateTime.UtcNow.Ticks of most recent tick, or 0
    private string? _lastTickSymbol;
    private string[] _subscribed = Array.Empty<string>();
    private string? _lastError;

    public TickHubClient(
        IOptions<AgentOptions> options,
        TickDispatcher dispatcher,
        SubscriptionManager subscriptions,
        ILogger<TickHubClient> logger)
    {
        _options = options.Value;
        _dispatcher = dispatcher;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    /// <summary>Configured hub URL (copied from options so the Manager can display it even when disconnected).</summary>
    public string HubUrl => _options.HubUrl ?? string.Empty;

    /// <summary>Current SignalR connection state.</summary>
    public HubConnectionState ConnectionState =>
        _connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>SignalR connection id, available only once connected.</summary>
    public string? ConnectionId => _connection?.ConnectionId;

    /// <summary>Symbols we told the hub we want - the canonical, normalised set.</summary>
    public IReadOnlyList<string> SubscribedSymbols => _subscribed;

    /// <summary>Total ticks received from the hub since the Agent started.</summary>
    public long TicksReceived => Interlocked.Read(ref _ticksReceived);

    /// <summary>UTC time of the most recent tick received from the hub, or null if none yet.</summary>
    public DateTime? LastTickUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastTickTicks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }

    /// <summary>Symbol of the most recent tick received, or null if none yet.</summary>
    public string? LastTickSymbol => Volatile.Read(ref _lastTickSymbol);

    /// <summary>Most recent error surfaced by the hub connection; null once a fresh connection succeeds.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.HubUrl))
        {
            _logger.LogError("Agent:HubUrl is empty - cannot start hub client.");
            Volatile.Write(ref _lastError, "HubUrl is empty");
            return;
        }

        _subscribed = _subscriptions.Current.ToArray();
        _subscriptions.Changed += OnSubscriptionsChanged;

        _logger.LogInformation(
            "Agent connecting to {Url} ; subscribing to {Count} symbols: {Symbols}",
            _options.HubUrl, _subscribed.Length, string.Join(", ", _subscribed));

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
            Volatile.Write(ref _lastError, ex?.Message);
            return Task.CompletedTask;
        };
        _connection.Reconnected += async _ =>
        {
            _logger.LogInformation("Hub reconnected - re-applying subscriptions.");
            Volatile.Write(ref _lastError, null);
            // Use the manager's current set rather than the stale _subscribed snapshot so any
            // edits that happened while we were disconnected are applied on reconnect.
            _subscribed = _subscriptions.Current.ToArray();
            if (_subscribed.Length > 0)
            {
                try { await _connection.InvokeAsync("Subscribe", _subscribed, stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Re-subscribe after reconnect failed"); }
            }
        };
        _connection.Closed += ex =>
        {
            _logger.LogWarning(ex, "Hub connection closed.");
            Volatile.Write(ref _lastError, ex?.Message ?? "connection closed");
            return Task.CompletedTask;
        };

        // Outer retry loop: if even the first connect fails (server not up yet) we keep trying.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _connection.StartAsync(stoppingToken);
                _logger.LogInformation("Hub connected ({ConnectionId}).", _connection.ConnectionId);
                Volatile.Write(ref _lastError, null);

                if (_subscribed.Length > 0)
                {
                    await _connection.InvokeAsync("Subscribe", _subscribed, stoppingToken);
                    _logger.LogInformation("Subscribed to {Count} symbols.", _subscribed.Length);
                }
                break;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                var delay = Math.Max(1, _options.ReconnectDelaySeconds);
                _logger.LogWarning(ex, "Hub connect failed; retrying in {Delay}s...", delay);
                Volatile.Write(ref _lastError, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        // Wait until the host shuts us down.
        var tcs = new TaskCompletionSource();
        using var reg = stoppingToken.Register(() => tcs.TrySetResult());
        await tcs.Task;

        _subscriptions.Changed -= OnSubscriptionsChanged;
        try { await _connection.StopAsync(CancellationToken.None); }
        catch { /* shutdown */ }
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// Applies a subscription diff to the live hub connection. Fires via the
    /// <see cref="SubscriptionManager.Changed"/> event; tolerates the "not connected yet"
    /// state by just updating <see cref="_subscribed"/> - whatever the current set is when
    /// we next (re)connect, that's what we'll send to the server.
    /// </summary>
    private async void OnSubscriptionsChanged(object? sender, SubscriptionChange change)
    {
        _subscribed = change.Current;

        var conn = _connection;
        if (conn is null || conn.State != HubConnectionState.Connected)
        {
            _logger.LogDebug("Subscriptions changed while disconnected; will apply on next connect.");
            return;
        }

        try
        {
            if (change.Added.Length > 0)
                await conn.InvokeAsync("Subscribe", change.Added);
            if (change.Removed.Length > 0)
                await conn.InvokeAsync("Unsubscribe", change.Removed);
            _logger.LogInformation(
                "Applied live subscription diff: +{Added} -{Removed}.",
                change.Added.Length, change.Removed.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply live subscription diff to hub.");
        }
    }

    private async Task OnTick(TickPayload payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.Symbol)) return;

        Interlocked.Increment(ref _ticksReceived);
        Interlocked.Exchange(ref _lastTickTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _lastTickSymbol, payload.Symbol);

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
