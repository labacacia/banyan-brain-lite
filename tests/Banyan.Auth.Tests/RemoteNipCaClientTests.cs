using Banyan.Auth;
using Banyan.Node.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NPS.NIP.Crypto;
using NSec.Cryptography;
using Xunit;

namespace Banyan.Auth.Tests;

/// <summary>
/// Spins up a real ASP.NET Core host with <see cref="NipCaEndpoints"/> mounted on top of an
/// <see cref="EmbeddedNipCa"/>, then exercises <see cref="RemoteNipCaClient"/> against it.
/// </summary>
public sealed class RemoteNipCaClientTests : IAsyncLifetime
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-remote-ca-" + Guid.NewGuid().ToString("N")[..8]);
    private const string  Passphrase = "round-trip-2026";

    private WebApplication?     _app;
    private EmbeddedNipCa?      _ca;
    private RemoteNipCaClient   _client = null!;
    private string              _baseUrl = "";

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

        _baseUrl = _app.Urls.First();
        _client = new RemoteNipCaClient(_baseUrl);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
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
    public async Task Health_Returns_True()
    {
        Assert.True(await _client.HealthAsync());
    }

    [Fact]
    public async Task WellKnown_AdvertisesIssuerAndAlgo()
    {
        var d = await _client.WellKnownAsync();
        Assert.NotNull(d);
        Assert.Equal("urn:nps:ca:test.banyan:root", d!.Issuer);
        Assert.Contains("ed25519", d.Algorithms);
        Assert.Contains("agent",   d.Capabilities);
        Assert.Contains("node",    d.Capabilities);
    }

    [Fact]
    public async Task CaCert_ReturnsCAPublicKey()
    {
        var c = await _client.CaCertAsync();
        Assert.NotNull(c);
        Assert.Equal("urn:nps:ca:test.banyan:root", c!.Nid);
        Assert.StartsWith("ed25519:", c.PubKey);
    }

    [Fact]
    public async Task RegisterAgent_RoundTrip_AndVerify()
    {
        var resp = await _client.RegisterAgentAsync("alpha", GenAgentPubKey(), new[] { "memory.read" });
        Assert.Contains(":alpha", resp.Nid);
        Assert.NotNull(resp.IdentFrame);
        Assert.NotEmpty(resp.Serial);

        var v = await _client.VerifyAsync(resp.Nid);
        Assert.True(v.Valid);
        Assert.Equal(resp.Nid, v.Nid);
        Assert.Equal("agent", v.EntityType);
    }

    [Fact]
    public async Task Verify_UnknownNid_Returns404Body()
    {
        var v = await _client.VerifyAsync("urn:nps:agent:test.banyan:ghost");
        Assert.False(v.Valid);
        Assert.Equal("NIP-CA-NID-NOT-FOUND", v.ErrorCode);
    }

    [Fact]
    public async Task Revoke_ThenVerify_ReturnsRevokedError()
    {
        var resp = await _client.RegisterAgentAsync("bravo", GenAgentPubKey(), Array.Empty<string>());
        var rev  = await _client.RevokeAsync(resp.Nid, "smoke-revoke");
        Assert.Equal(resp.Nid, rev.Nid);
        Assert.Equal("smoke-revoke", rev.Reason);

        var v = await _client.VerifyAsync(resp.Nid);
        Assert.False(v.Valid);
        Assert.Equal("NIP-CERT-REVOKED", v.ErrorCode);
    }

    [Fact]
    public async Task RegisterNode_TagsEntityType()
    {
        var resp = await _client.RegisterNodeAsync("node-1", GenAgentPubKey(), Array.Empty<string>());
        Assert.Contains(":node:", resp.Nid);

        var v = await _client.VerifyAsync(resp.Nid);
        Assert.True(v.Valid);
        Assert.Equal("node", v.EntityType);
    }
}
