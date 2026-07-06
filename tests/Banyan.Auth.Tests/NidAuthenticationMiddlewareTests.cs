// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Banyan.Auth;
using Banyan.Node.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.Verification;
using NSec.Cryptography;
using Xunit;

namespace Banyan.Auth.Tests;

/// <summary>
/// End-to-end behaviour of <see cref="NidAuthenticationMiddleware"/> across all three
/// <see cref="NidAuthMode"/> values, exercised through real HTTP against an in-process app.
/// </summary>
public sealed class NidAuthenticationMiddlewareTests : IAsyncLifetime
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-nid-auth-" + Guid.NewGuid().ToString("N")[..8]);
    private const string  Passphrase = "auth-mw-2026";

    private WebApplication?    _app;
    private EmbeddedNipCa?     _ca;
    private HttpClient         _http  = null!;
    private string             _baseUrl = "";

    private NidAuthenticationOptions _authOpts = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tmpDir);
        var bn = new BanyanNipCaOptions
        {
            DbPath        = Path.Combine(_tmpDir, "nipca.db"),
            KeyFilePath   = Path.Combine(_tmpDir, "ca-key.pem"),
            KeyPassphrase = Passphrase,
            CaNid         = "urn:nps:ca:test.banyan:root",
            BaseUrl       = "http://localhost:0",
        };
        EmbeddedNipCa.GenerateKey(bn);
        _ca = await EmbeddedNipCa.OpenAsync(bn);

        _authOpts = new NidAuthenticationOptions { Mode = NidAuthMode.AnonymousAllowed };

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_ca);
        builder.Services.AddSingleton(_authOpts);
        builder.Services.AddSingleton(_ => new NipVerifierOptions
        {
            TrustedIssuers      = new Dictionary<string, string> { [_ca.CaNid] = _ca.CaPubKey },
            LocalRevokedSerials = new HashSet<string>(),
            OcspUrl              = null!,
        });
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<NipIdentVerifier>();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();

        _app.UseNidAuthentication();

        // A liveness route (public path) and a sample protected route under /api.
        _app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
        _app.MapGet("/api/protected", (HttpContext ctx) =>
            Results.Ok(new
            {
                nid = ctx.Items[NidAuthenticationOptions.ContextKeyNid] as string ?? "(none)",
            }));
        _app.MapPost("/api/protected", (HttpContext ctx) =>
            Results.Ok(new
            {
                nid = ctx.Items[NidAuthenticationOptions.ContextKeyNid] as string ?? "(none)",
            }));
        _app.MapPost("/mcp", (HttpContext ctx) =>
            Results.Ok(new
            {
                nid = ctx.Items[NidAuthenticationOptions.ContextKeyNid] as string ?? "(none)",
            }));
        // Mount the CA endpoints so we can issue a real frame in tests below.
        NipCaEndpoints.Map(_app);

        await _app.StartAsync();
        _baseUrl = _app.Urls.First();
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null) await _app.StopAsync();
        if (_app is not null) await _app.DisposeAsync();
        if (_ca is not null) await _ca.DisposeAsync();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private async Task<IdentFrame> IssueFrameAsync(string id = "tester")
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var pubKey = NipSigner.EncodePublicKey(key.PublicKey);
        return await _ca!.RegisterAgentAsync(id, pubKey, new[] { "memory.read", "memory.write" });
    }

    // ── AnonymousAllowed ───────────────────────────────────────────────────

    [Fact]
    public async Task AnonymousAllowed_NoHeader_PassesThrough()
    {
        _authOpts.Mode = NidAuthMode.AnonymousAllowed;
        var resp = await _http.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("(none)", body.GetProperty("nid").GetString());
    }

    [Fact]
    public async Task AnonymousAllowed_ValidHeader_PopulatesNid()
    {
        _authOpts.Mode = NidAuthMode.AnonymousAllowed;
        var frame = await IssueFrameAsync("alice");

        // Sanity check: the verifier itself accepts the frame (rules out NPS-side regressions
        // dragging the middleware test). If this fails the issue is upstream of us.
        var verifier = _app!.Services.GetRequiredService<NipIdentVerifier>();
        var direct = await verifier.VerifyAsync(frame, new NipVerifyContext { AsOf = DateTime.UtcNow });
        Assert.True(direct.IsValid, $"verifier rejected freshly issued frame: {direct.ErrorCode} {direct.Message}");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        req.Headers.Add("Authorization", NidAuthHeader.Build(frame));
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(frame.Nid, body.GetProperty("nid").GetString());
    }

    [Fact]
    public async Task AnonymousAllowed_McpPostWithoutAuth_StillAllowed()
    {
        _authOpts.Mode = NidAuthMode.AnonymousAllowed;
        var resp = await _http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("(none)", body.GetProperty("nid").GetString());
    }

    [Fact]
    public async Task AnonymousAllowed_InvalidHeader_PassesAsAnon()
    {
        _authOpts.Mode = NidAuthMode.AnonymousAllowed;
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        req.Headers.Add("Authorization", "NID not-a-base64");
        var resp = await _http.SendAsync(req);
        // Tolerant — invalid header is treated as "no header" in anon mode
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("(none)", body.GetProperty("nid").GetString());
    }

    // ── WritesRequired ─────────────────────────────────────────────────────

    [Fact]
    public async Task WritesRequired_GetWithoutAuth_StillAllowed()
    {
        _authOpts.Mode = NidAuthMode.WritesRequired;
        var resp = await _http.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task WritesRequired_PostWithoutAuth_Returns401()
    {
        _authOpts.Mode = NidAuthMode.WritesRequired;
        var resp = await _http.PostAsync("/api/protected", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NIP-AUTH-REQUIRED", body.GetProperty("error_code").GetString());
        Assert.Equal("NID realm=\"banyan\"", resp.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task WritesRequired_McpPostWithoutAuth_Returns401()
    {
        _authOpts.Mode = NidAuthMode.WritesRequired;
        var resp = await _http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NIP-AUTH-REQUIRED", body.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task WritesRequired_PostWithValidAuth_PassesThrough()
    {
        _authOpts.Mode = NidAuthMode.WritesRequired;
        var frame = await IssueFrameAsync("bob");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/protected") { Content = new StringContent("{}") };
        req.Headers.Add("Authorization", NidAuthHeader.Build(frame));
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── AllRequired ────────────────────────────────────────────────────────

    [Fact]
    public async Task AllRequired_GetWithoutAuth_Returns401()
    {
        _authOpts.Mode = NidAuthMode.AllRequired;
        var resp = await _http.GetAsync("/api/protected");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AllRequired_HealthAndManifest_StillPublic()
    {
        _authOpts.Mode = NidAuthMode.AllRequired;
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/v1/ca/cert")).StatusCode);
    }

    [Fact]
    public async Task AllRequired_TamperedFrame_Returns401()
    {
        _authOpts.Mode = NidAuthMode.AllRequired;
        var frame = await IssueFrameAsync("eve");
        // Forge: keep everything but flip the signature so verifier rejects it.
        var forged = frame with { Signature = "ed25519:AAAAAAAAAAAAAAAA" };
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        req.Headers.Add("Authorization", NidAuthHeader.Build(forged));
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AllRequired_RevokedFrame_Returns401()
    {
        _authOpts.Mode = NidAuthMode.AllRequired;
        var frame = await IssueFrameAsync("mallory");
        await _ca!.RevokeAsync(frame.Nid, "test-revoke");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        req.Headers.Add("Authorization", NidAuthHeader.Build(frame));
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
