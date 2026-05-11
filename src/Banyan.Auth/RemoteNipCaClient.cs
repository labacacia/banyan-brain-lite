using System.Net.Http.Json;
using System.Text.Json;
using NPS.NIP.Frames;

namespace Banyan.Auth;

/// <summary>
/// HTTP client for an NPS-3 §8 conformant NIP CA server (path layout from
/// <see href="https://github.com/labacacia/nip-ca-server"/>). Mirrors the call shapes of
/// <see cref="EmbeddedNipCa"/> so callers can swap in/out by configuration.
/// </summary>
public sealed class RemoteNipCaClient : IAsyncDisposable, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool       _ownsHttp;
    public string Endpoint { get; }

    public RemoteNipCaClient(string endpoint, HttpClient? http = null)
    {
        Endpoint = endpoint.TrimEnd('/');
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    public sealed record IssueResponse(string Nid, string Serial, string IssuedAt, string ExpiresAt, IdentFrame? IdentFrame);
    public sealed record VerifyResponse(bool Valid, string Nid, string? EntityType, string? PubKey, string[]? Capabilities,
        string? IssuedBy, string? IssuedAt, string? ExpiresAt, string? Serial, string? ErrorCode, string? Message);
    public sealed record RevokeResponse(string Nid, string? RevokedAt, string Reason);
    public sealed record CaCertResponse(string Nid, string DisplayName, string PubKey, string Algorithm);
    public sealed record WellKnownResponse(string NpsCa, string Issuer, string DisplayName, string PublicKey, string[] Algorithms,
        string[] CertFormats, IReadOnlyDictionary<string, string> Endpoints, string[] Capabilities, int MaxCertValidityDays);

    /// <summary>
    /// Request bodies use explicit snake_case dictionary keys (NPS wire format),
    /// so we don't apply <see cref="JsonNamingPolicy.SnakeCaseLower"/> — it would re-mangle
    /// already-snake-cased keys. Response deserialization uses snake-case naming so
    /// CLR PascalCase property names line up with NPS JSON.
    /// </summary>
    private static readonly JsonSerializerOptions s_request = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions s_response = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ── Health / discovery ──────────────────────────────────────────────────

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{Endpoint}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<WellKnownResponse?> WellKnownAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<WellKnownResponse>($"{Endpoint}/.well-known/nps-ca", s_response, ct);

    public async Task<CaCertResponse?> CaCertAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<CaCertResponse>($"{Endpoint}/v1/ca/cert", s_response, ct);

    // ── Register agent / node ────────────────────────────────────────────────

    public Task<IssueResponse> RegisterAgentAsync(string identifier, string pubKey, string[] capabilities,
        JsonElement? scope = null, JsonElement? metadata = null, CancellationToken ct = default)
        => RegisterAsync("/v1/agents/register", identifier, pubKey, capabilities, scope, metadata, ct);

    public Task<IssueResponse> RegisterNodeAsync(string identifier, string pubKey, string[] capabilities,
        JsonElement? scope = null, JsonElement? metadata = null, CancellationToken ct = default)
        => RegisterAsync("/v1/nodes/register", identifier, pubKey, capabilities, scope, metadata, ct);

    private async Task<IssueResponse> RegisterAsync(
        string path, string identifier, string pubKey, string[] capabilities,
        JsonElement? scope, JsonElement? metadata, CancellationToken ct)
    {
        // Pass the identifier as a partial NID hint so the server's BuildNid logic uses it verbatim.
        // The server treats `nid` as optional and parses out the trailing identifier segment.
        // Names are matched case-insensitively on the server side; explicit snake_case below
        // matches the NPS spec verbatim regardless of whether NamingPolicy fires.
        var body = new Dictionary<string, object?>
        {
            ["nid"]          = identifier,
            ["pub_key"]      = pubKey,
            ["capabilities"] = capabilities,
            ["scope"]        = scope    ?? JsonDocument.Parse("{}").RootElement,
            ["metadata"]     = metadata ?? JsonDocument.Parse("{}").RootElement,
        };
        var resp = await _http.PostAsJsonAsync($"{Endpoint}{path}", body, s_request, ct);
        await ThrowIfNotSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<IssueResponse>(s_response, ct))!;
    }

    public async Task<IssueResponse> RenewAsync(string nid, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"{Endpoint}/v1/agents/{Uri.EscapeDataString(nid)}/renew", content: null, ct);
        await ThrowIfNotSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<IssueResponse>(s_response, ct))!;
    }

    public async Task<RevokeResponse> RevokeAsync(string nid, string? reason = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["reason"] = reason ?? "operator-initiated" };
        var resp = await _http.PostAsJsonAsync($"{Endpoint}/v1/agents/{Uri.EscapeDataString(nid)}/revoke", body, s_request, ct);
        await ThrowIfNotSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<RevokeResponse>(s_response, ct))!;
    }

    public async Task<VerifyResponse> VerifyAsync(string nid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{Endpoint}/v1/agents/{Uri.EscapeDataString(nid)}/verify", ct);
        // 404 also returns a JSON body with error_code; deserialize uniformly.
        return (await resp.Content.ReadFromJsonAsync<VerifyResponse>(s_response, ct))!;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task ThrowIfNotSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new RemoteNipCaException((int)resp.StatusCode, body);
    }
}

public sealed class RemoteNipCaException(int status, string body)
    : Exception($"NIP-CA HTTP {status}: {body}")
{
    public int StatusCode { get; } = status;
    public string Body { get; } = body;
}
