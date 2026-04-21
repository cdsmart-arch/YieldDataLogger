namespace YieldDataLogger.Core.Models;

/// <summary>
/// Point-in-time snapshot of a running Agent process. Serialised to JSON at a well-known
/// path (by default %ProgramData%\YieldDataLogger\Agent\status.json) every second by the
/// Agent's StatusWriterService, and polled by the Manager tray app to render a live
/// observability dashboard without needing sockets or IPC between the two processes.
///
/// This is intentionally a simple record so System.Text.Json can handle it on both sides
/// with no custom converters. Version the schema if you ever break a field - the Manager
/// checks SchemaVersion on load and degrades gracefully if it does not recognise it.
/// </summary>
public sealed record AgentStatus
{
    /// <summary>Schema version; bump whenever a breaking change is made to the fields below.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>ISO-8601 UTC timestamp of when this snapshot was written.</summary>
    public DateTime WrittenAtUtc { get; init; }

    /// <summary>When the Agent process started, in UTC. Used to render uptime.</summary>
    public DateTime StartedAtUtc { get; init; }

    /// <summary>Process id - lets the Manager sanity-check that the Agent it thinks is running, really is.</summary>
    public int Pid { get; init; }

    /// <summary>Machine name, purely for display in multi-PC setups.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>The hub URL the Agent is configured to talk to.</summary>
    public string HubUrl { get; init; } = string.Empty;

    /// <summary>
    /// Base URL of the REST API (e.g. http://localhost:5055). Surfaced so the Manager can
    /// call <c>GET /api/instruments</c> without also needing its own config - the Agent is
    /// already the single source of truth for "where does this installation talk to".
    /// </summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>Current SignalR connection state: Disconnected / Connecting / Connected / Reconnecting / Disconnecting.</summary>
    public string ConnectionState { get; init; } = "Disconnected";

    /// <summary>The SignalR connection id once connected; null while connecting or after a disconnect.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>Symbols the Agent is currently subscribed to on the hub (post-dedup canonical form).</summary>
    public IReadOnlyList<string> SubscribedSymbols { get; init; } = Array.Empty<string>();

    /// <summary>Timestamp of the most recent tick the Agent received from the hub, or null if none yet.</summary>
    public DateTime? LastTickUtc { get; init; }

    /// <summary>Symbol of the most recent tick the Agent received, or null if none yet.</summary>
    public string? LastTickSymbol { get; init; }

    /// <summary>Total ticks received from the hub since startup.</summary>
    public long TicksReceived { get; init; }

    /// <summary>Ticks locally suppressed by the Agent's TickDispatcher (incoming price matched last seen).</summary>
    public long TicksDeduped { get; init; }

    /// <summary>Ticks that actually made it past dedup and were written to local sinks.</summary>
    public long TicksDispatched { get; init; }

    /// <summary>Absolute path the SqliteSink is writing to (empty string if that sink is disabled).</summary>
    public string SqliteSinkPath { get; init; } = string.Empty;

    /// <summary>Absolute path the ScidSink is writing to (empty string if that sink is disabled).</summary>
    public string ScidSinkPath { get; init; } = string.Empty;

    /// <summary>Free-form last error message, if any (e.g. hub unreachable). Cleared on successful reconnect.</summary>
    public string? LastError { get; init; }
}
