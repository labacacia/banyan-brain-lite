// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NPS.NIP.Frames;

namespace Banyan.Auth;

/// <summary>
/// HTTP client for a canonical NPS NIP CA server — the contract served by the SDK
/// <c>NPS.NIP.Http.NipCaRouter</c> (and <see href="https://github.com/labacacia/nip-ca-server"/>).
/// Speaks the SDK wire shapes: register/renew return a bare <see cref="IdentFrame"/>, revoke a bare
/// <see cref="RevokeFrame"/>, verify the OCSP-style status body. Returns the same SDK frame types as
/// <see cref="EmbeddedNipCa"/> so callers can swap embedded/remote by configuration.
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

    /// <summary>OCSP-style verify status (NipCaRouter <c>/v1/{agents|nodes}/{nid}/verify</c>).</summary>
    public sealed record VerifyResponse(bool Valid, string? Nid, string? ExpiresAt, string? Serial, string? ErrorCode, string? Message);

    /// <summary>CA public key (NipCaRouter <c>/v1/ca/cert</c> — note: no NID/display_name; use <see cref="WellKnownAsync"/> for those).</summary>
    public sealed record CaCertResponse(string PublicKey, string Algorithm);

    /// <summary>CA discovery doc (NipCaRouter <c>/.well-known/nps-ca</c>). <see cref="Issuer"/> is the CA NID.</summary>
    public sealed record WellKnownResponse(string NpsCa, string Issuer, string? DisplayName, string PublicKey,
        string[] Algorithms, IReadOnlyDictionary<string, string> Endpoints, string[] Capabilities, int MaxCertValidityDays);

    // Request keys are explicit snake_case (no naming policy so they aren't re-mangled).
    private static readonly JsonSerializerOptions s_request = new() { PropertyNameCaseInsensitive = true };
    // Responses are snake_case NPS frames; SnakeCaseLower + case-insensitive lines CLR props up with the wire.
    private static readonly JsonSerializerOptions s_response = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
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

    public Task<IdentFrame> RegisterAgentAsync(string identifier, string pubKey, string[] capabilities,
        JsonElement? scope = null, JsonElement? metadata = null, CancellationToken ct = default)
        => RegisterAsync("/v1/agents/register", identifier, pubKey, capabilities, scope, metadata, ct);

    public Task<IdentFrame> RegisterNodeAsync(string identifier, string pubKey, string[] capabilities,
        JsonElement? scope = null, JsonElement? metadata = null, CancellationToken ct = default)
        => RegisterAsync("/v1/nodes/register", identifier, pubKey, capabilities, scope, metadata, ct);

    private async Task<IdentFrame> RegisterAsync(
        string path, string identifier, string pubKey, string[] capabilities,
        JsonElement? scope, JsonElement? metadata, CancellationToken ct)
    {
        // SDK RegisterRequest: identifier + pub_key + capabilities + scope_json/metadata_json (JSON strings).
        var body = new Dictionary<string, object?>
        {
            ["identifier"]    = identifier,
            ["pub_key"]       = pubKey,
            ["capabilities"]  = capabilities,
            ["scope_json"]    = scope?.GetRawText()    ?? "{}",
            ["metadata_json"] = metadata?.GetRawText() ?? "{}",
        };
        var resp = await _http.PostAsJsonAsync($"{Endpoint}{path}", body, s_request, ct);
        await ThrowIfNotSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<IdentFrame>(s_response, ct))!;
    }

    public async Task<IdentFrame> RenewAsync(string nid, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"{Endpoint}/v1/agents/{Uri.EscapeDataString(nid)}/renew", content: null, ct);
        await ThrowIfNotSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<IdentFrame>(s_response, ct))!;
    }

    public async Task<RevokeFrame> RevokeAsync(string nid, string? reason = null, CancellationToken ct = default)
    {
        // Reason must be one of the SDK's allowed revocation reasons; default to the spec's catch-all.
        var body = new Dictionary<string, object?> { ["reason"] = reason ?? "cessation_of_operation" };
        var resp = await _http.PostAsJsonAsync($"{Endpoint}/v1/agents/{Uri.EscapeDataString(nid)}/revoke", body, s_request, ct);
        await ThrowIfNotSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<RevokeFrame>(s_response, ct))!;
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
