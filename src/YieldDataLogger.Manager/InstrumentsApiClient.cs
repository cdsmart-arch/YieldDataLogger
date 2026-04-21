using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace YieldDataLogger.Manager;

/// <summary>
/// Thin client over the Api's /api/instruments endpoint. Base URL is supplied by the
/// caller (typically <c>AgentStatus.ApiBaseUrl</c>) rather than configured independently,
/// so whichever API the running Agent is pointing at is the one the Manager queries.
/// </summary>
internal sealed class InstrumentsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public InstrumentsApiClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<IReadOnlyList<InstrumentRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<InstrumentRecord>>("api/instruments", JsonOptions, ct);
        return list ?? new List<InstrumentRecord>();
    }

    /// <summary>
    /// Shape mirrors the server's <c>InstrumentDto</c>. Kept as a Manager-local record so
    /// we don't drag the Api reference chain into the Manager project.
    /// </summary>
    public sealed record InstrumentRecord(
        string CanonicalSymbol,
        int? InvestingPid,
        string? CnbcSymbol,
        string? Category)
    {
        public string Source => InvestingPid.HasValue && !string.IsNullOrWhiteSpace(CnbcSymbol)
            ? "investing + cnbc"
            : InvestingPid.HasValue
                ? "investing"
                : !string.IsNullOrWhiteSpace(CnbcSymbol)
                    ? "cnbc"
                    : "-";
    }
}
