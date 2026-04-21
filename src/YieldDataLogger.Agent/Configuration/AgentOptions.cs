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

    /// <summary>Canonical symbols this agent wants ticks for.</summary>
    public string[] Symbols { get; set; } = Array.Empty<string>();

    /// <summary>Optional bearer token attached to the hub connection. Empty = no auth (current default).</summary>
    public string? AuthToken { get; set; }

    /// <summary>Seconds to wait between automatic reconnect attempts; SignalR's default schedule otherwise.</summary>
    public int ReconnectDelaySeconds { get; set; } = 5;

    public AgentSinksOptions Sinks { get; set; } = new();
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
