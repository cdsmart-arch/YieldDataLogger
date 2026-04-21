using Microsoft.AspNetCore.SignalR;

namespace YieldDataLogger.Api.Realtime;

/// <summary>
/// SignalR hub for real-time price tick delivery. Clients connect, then call
/// <see cref="Subscribe"/> with a list of canonical symbols; the server joins
/// them to a group per symbol and <see cref="SignalRPriceSink"/> pushes ticks
/// there as they arrive. Group names are uppercased canonical symbols.
///
/// No auth yet (Phase 3c adds [Authorize] + admin-role checks). Anonymous
/// clients can currently subscribe to any symbol that exists in the catalog.
/// </summary>
public sealed class TicksHub : Hub
{
    private const string SymbolsKey = "subscribed";

    /// <summary>Subscribe this connection to zero or more canonical symbols.</summary>
    public async Task Subscribe(string[] symbols)
    {
        if (symbols is null || symbols.Length == 0) return;
        var set = GetOrCreate();
        foreach (var raw in symbols)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var sym = raw.Trim().ToUpperInvariant();
            if (set.Add(sym))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, sym);
            }
        }
    }

    /// <summary>Unsubscribe from the given symbols. Missing symbols are ignored.</summary>
    public async Task Unsubscribe(string[] symbols)
    {
        if (symbols is null || symbols.Length == 0) return;
        var set = GetOrCreate();
        foreach (var raw in symbols)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var sym = raw.Trim().ToUpperInvariant();
            if (set.Remove(sym))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, sym);
            }
        }
    }

    /// <summary>Returns this connection's current subscriptions.</summary>
    public Task<string[]> GetSubscriptions()
    {
        var set = Context.Items.TryGetValue(SymbolsKey, out var v) && v is HashSet<string> s
            ? s.ToArray()
            : Array.Empty<string>();
        return Task.FromResult(set);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR cleans up group memberships automatically when a connection drops;
        // no manual bookkeeping needed on disconnect.
        return base.OnDisconnectedAsync(exception);
    }

    private HashSet<string> GetOrCreate()
    {
        if (Context.Items.TryGetValue(SymbolsKey, out var v) && v is HashSet<string> existing)
            return existing;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Context.Items[SymbolsKey] = set;
        return set;
    }
}
