// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Node.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.NIP.Client;
using NPS.NIP.Crypto;
using NSec.Cryptography;
using Xunit;

namespace Banyan.Auth.Tests;

/// <summary>
/// Spins up a real ASP.NET Core host with <see cref="NipCaEndpoints"/> mounted on top of an
/// <see cref="EmbeddedNipCa"/>, then exercises the official SDK <see cref="NipCaClient"/> against
/// it — Banyan's remote-CA path now uses the SDK client rather than a hand-rolled HTTP shim.
/// </summary>
public sealed class NipCaClientTests : IAsyncLifetime
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-remote-ca-" + Guid.NewGuid().ToString("N")[..8]);
    private const string  Passphrase = "round-trip-2026";

    private WebApplication? _app;
    private EmbeddedNipCa?  _ca;
    private HttpClient      _http = null!;
    private NipCaClient     _client = null!;

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

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(_ca);
        builder.WebHost.UseUrls("http://127.0.0.1:0");  // ephemeral port
        _app = builder.Build();
        NipCaEndpoints.Map(_app);
        await _app.StartAsync();

        var baseUrl = _app.Urls.First();
        _http   = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _client = new NipCaClient(_http);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null) await _app.StopAsync();
        if (_app is not null) await _app.DisposeAsync();
        if (_ca is not null) await _ca.DisposeAsync();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private static string GenAgentPubKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return NipSigner.EncodePublicKey(key.PublicKey);
    }

    [Fact]
    public async Task Discovery_AdvertisesIssuerAndAlgo()
    {
        var d = await _client.GetDiscoveryAsync();
        Assert.Equal("urn:nps:ca:test.banyan:root", d.Issuer);
        Assert.StartsWith("ed25519:", d.PublicKey);
        Assert.NotNull(d.Algorithms);
        Assert.Contains("ed25519", d.Algorithms!);
        Assert.NotNull(d.Capabilities);
        Assert.Contains("agent", d.Capabilities!);
        Assert.Contains("node",  d.Capabilities!);
    }

    [Fact]
    public async Task RegisterAgent_RoundTrip_AndVerify()
    {
        var frame = await _client.RegisterAgentAsync(
            new NipCaRegisterRequest("alpha", GenAgentPubKey(), new[] { "memory.read" }));
        Assert.Contains(":alpha", frame.Nid);
        Assert.NotEmpty(frame.Serial);
        Assert.NotEmpty(frame.Signature);

        var v = await _client.VerifyAgentAsync(frame.Nid);
        Assert.True(v.Valid);
        Assert.Equal(frame.Nid, v.Nid);
    }

    [Fact]
    public async Task Verify_UnknownNid_Throws404()
    {
        // The SDK client surfaces a not-found NID as a 404 NipCaClientException, not a valid=false body.
        var ex = await Assert.ThrowsAsync<NipCaClientException>(
            () => _client.VerifyAgentAsync("urn:nps:agent:test.banyan:ghost"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Revoke_ThenVerify_ReturnsRevokedError()
    {
        var frame = await _client.RegisterAgentAsync(
            new NipCaRegisterRequest("bravo", GenAgentPubKey(), Array.Empty<string>()));
        var rev = await _client.RevokeAgentAsync(frame.Nid, "cessation_of_operation");
        Assert.Equal(frame.Nid, rev.TargetNid);
        Assert.Equal("cessation_of_operation", rev.Reason);

        var v = await _client.VerifyAgentAsync(frame.Nid);
        Assert.False(v.Valid);
    }

    [Fact]
    public async Task RegisterNode_TagsEntityType()
    {
        var frame = await _client.RegisterNodeAsync(
            new NipCaRegisterRequest("node-1", GenAgentPubKey(), Array.Empty<string>()));
        Assert.Contains(":node:", frame.Nid);

        var v = await _client.VerifyNodeAsync(frame.Nid);
        Assert.True(v.Valid);
    }
}
