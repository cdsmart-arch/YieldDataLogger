namespace YieldDataLogger.Api.Auth;

/// <summary>
/// Shared-secret gate for the API. The Hetzner deploy is on the public internet, so every
/// request to /api/* and /hubs/* must present the secret. Accepts three forms, in order:
///
///   1. <c>X-Ingest-Secret: &lt;secret&gt;</c>     — used by REST clients (Agent backfill)
///   2. <c>Authorization: Bearer &lt;secret&gt;</c> — SignalR's .NET client sends the token here
///   3. <c>?access_token=&lt;secret&gt;</c>         — SignalR WebSocket upgrade in browsers
///
/// The middleware is a no-op when <see cref="IngestSecretOptions.Secret"/> is empty (local
/// dev, the original behaviour). <c>/healthz</c>, the root redirect, and the admin SPA
/// static files are always public so external uptime checks and operators reach them.
/// </summary>
public sealed class IngestSecretMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _secret;
    private readonly ILogger<IngestSecretMiddleware> _logger;

    public IngestSecretMiddleware(RequestDelegate next, IngestSecretOptions options, ILogger<IngestSecretMiddleware> logger)
    {
        _next = next;
        _secret = string.IsNullOrWhiteSpace(options.Secret) ? null : options.Secret;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (_secret is null)
        {
            await _next(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? "";
        if (IsPublic(path))
        {
            await _next(ctx);
            return;
        }

        if (IsAuthorized(ctx))
        {
            await _next(ctx);
            return;
        }

        _logger.LogInformation("Rejected unauthenticated request to {Path} from {Ip}",
            path, ctx.Connection.RemoteIpAddress);
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("missing or invalid ingest secret");
    }

    private static bool IsPublic(string path) =>
        path.Equals("/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);

    private bool IsAuthorized(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Ingest-Secret", out var headerVal)
            && SecretMatches(headerVal.ToString()))
            return true;

        if (ctx.Request.Headers.TryGetValue("Authorization", out var authVal))
        {
            var raw = authVal.ToString();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && SecretMatches(raw["Bearer ".Length..]))
                return true;
        }

        if (ctx.Request.Query.TryGetValue("access_token", out var queryVal)
            && SecretMatches(queryVal.ToString()))
            return true;

        return false;
    }

    private bool SecretMatches(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || _secret is null) return false;
        // Constant-time compare to avoid trivial timing side channels.
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(candidate),
            System.Text.Encoding.UTF8.GetBytes(_secret));
    }
}

public sealed class IngestSecretOptions
{
    public const string SectionName = "Auth";
    /// <summary>Shared secret. Empty = no auth required (local dev default).</summary>
    public string? Secret { get; set; }
}
