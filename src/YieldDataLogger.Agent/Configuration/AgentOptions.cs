namespace YieldDataLogger.Agent.Configuration;

/// <summary>
/// Bound from the "Agent" section of appsettings.json. A single config file drives everything
/// the local agent does: which hub to connect to, which symbols to subscribe, where to write.
///
/// AuthToken is reserved for future use - when Phase 3c/3d land, the bearer token goes here
/// (secured via user-secrets or DPAPI-protected file) and is attached to the hub connection.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Full hub URL, e.g. http://localhost:5055/hubs/ticks or https://api.example.com/hubs/ticks.</summary>
    public string HubUrl { get; set; } = "http://localhost:5055/hubs/ticks";

    /// <summary>
    /// Base URL of the REST API. Used by the Agent only to populate AgentStatus.ApiBaseUrl
    /// so the Manager knows where to fetch the instrument catalog. If left empty it is
    /// derived from HubUrl by stripping the "/hubs/..." suffix.
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Initial ("seed") subscriptions used when no subscriptions.json exists yet. Once a
    /// subscriptions file has been written (either by the Manager or by the Agent on first
    /// run) it becomes the authoritative source and this property is ignored.
    /// </summary>
    public string[] Symbols { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Absolute path to the persisted subscription list (JSON). Lives next to the status
    /// file by default. %ENV% variables are expanded.
    /// </summary>
    public string SubscriptionsPath { get; set; } =
        "%ProgramData%\\YieldDataLogger\\Agent\\subscriptions.json";

    /// <summary>Optional bearer token attached to the hub connection. Empty = no auth (current default).</summary>
    public string? AuthToken { get; set; }

    /// <summary>Seconds to wait between automatic reconnect attempts; SignalR's default schedule otherwise.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    public AgentSinksOptions Sinks { get; set; } = new();

    public AgentStatusWriterOptions Status { get; set; } = new();
}

/// <summary>
/// Controls the periodic status JSON the Agent writes so the Manager (and anything else
/// observing) can render a live dashboard without an IPC channel.
/// </summary>
public sealed class AgentStatusWriterOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Absolute path to the JSON status file. %ENV% variables are expanded; the parent
    /// directory is created on startup. ProgramData is the default because it's the canonical
    /// machine-wide location for service-managed state on Windows.
    /// </summary>
    public string Path { get; set; } = "%ProgramData%\\YieldDataLogger\\Agent\\status.json";

    /// <summary>How often to refresh the file. Defaults to every second.</summary>
    public int IntervalSeconds { get; set; } = 1;
}

public sealed class AgentSinksOptions
{
    public SqliteSinkOptions Sqlite { get; set; } = new();
    public ScidSinkOptions   Scid   { get; set; } = new();
}

public sealed class SqliteSinkOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Directory where per-symbol .sqlite files are created. %ENV% variables are expanded.</summary>
    public string Path { get; set; } = "%ProgramData%\\YieldDataLogger\\Yields";
}

public sealed class ScidSinkOptions
{
    public bool Enabled { get; set; } = false;
    /// <summary>Sierra Chart Data folder. %ENV% variables are expanded.</summary>
    public string Path { get; set; } = "C:\\SierraChart\\Data";
    /// <summary>Subset of subscribed symbols to actually write .scid for. Empty = none.</summary>
    public string[] AllowedSymbols { get; set; } = Array.Empty<string>();
}
