// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using NPS.NIP.Crypto;
using NSec.Cryptography;
using Xunit;

namespace Banyan.Auth.Tests;

public sealed class EmbeddedNipCaTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-nipca-" + Guid.NewGuid().ToString("N")[..8]);
    private const string  Passphrase = "test-pass-2026";

    public EmbeddedNipCaTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose()        { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    private BanyanNipCaOptions Opts() => new()
    {
        DbPath        = Path.Combine(_tmpDir, "nipca.db"),
        KeyFilePath   = Path.Combine(_tmpDir, "ca-key.pem"),
        KeyPassphrase = Passphrase,
        CaNid         = "urn:nps:ca:test.banyan:root",
        BaseUrl       = "http://localhost:5180",
        DisplayName   = "Test CA",
    };

    /// <summary>Generate an ed25519 keypair and encode the public half the way NipCaService expects.</summary>
    private static string GenAgentPubKey()
    {
        var algo = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return NipSigner.EncodePublicKey(key.PublicKey);
    }

    [Fact]
    public void GenerateKey_WritesEncryptedPemAtConfiguredPath()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);

        Assert.True(File.Exists(opts.KeyFilePath));
        Assert.NotEmpty(File.ReadAllText(opts.KeyFilePath));
    }

    [Fact]
    public void GenerateKey_RefusesOverwriteByDefault()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        Assert.Throws<IOException>(() => EmbeddedNipCa.GenerateKey(opts));
    }

    [Fact]
    public async Task OpenAsync_FailsWhenKeyMissing()
    {
        var opts = Opts(); // no GenerateKey beforehand
        await Assert.ThrowsAsync<FileNotFoundException>(() => EmbeddedNipCa.OpenAsync(opts));
    }

    [Fact]
    public async Task OpenAsync_FailsWhenPassphraseEmpty()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        opts.KeyPassphrase = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => EmbeddedNipCa.OpenAsync(opts));
    }

    [Fact]
    public async Task RegisterAgent_IssuesIdentFrame_WithMatchingNid()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        var frame = await ca.RegisterAgentAsync("alpha", GenAgentPubKey(), new[] { "memory.read" });

        Assert.NotNull(frame);
        Assert.Contains(":alpha", frame.Nid);
        Assert.NotEmpty(frame.Serial);
        Assert.NotEmpty(frame.Signature);
        Assert.Single(frame.Capabilities);
        Assert.Equal("memory.read", frame.Capabilities[0]);
    }

    [Fact]
    public async Task Verify_AfterRegister_ReturnsValid()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        var frame  = await ca.RegisterAgentAsync("bravo", GenAgentPubKey(), new[] { "memory.read" });
        var verify = await ca.VerifyAsync(frame.Nid);

        Assert.True(verify.Valid, $"expected Valid=true; got error={verify.ErrorCode} msg={verify.Message}");
        Assert.Equal(frame.Nid, verify.Record!.Nid);
    }

    [Fact]
    public async Task Revoke_ThenVerify_ReturnsInvalid()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        var frame = await ca.RegisterAgentAsync("charlie", GenAgentPubKey(), Array.Empty<string>());
        await ca.RevokeAsync(frame.Nid, "compromised");

        var verify = await ca.VerifyAsync(frame.Nid);
        Assert.False(verify.Valid);
    }

    [Fact]
    public async Task List_ReturnsAllIssuedAgents()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        await ca.RegisterAgentAsync("a1", GenAgentPubKey(), Array.Empty<string>());
        await ca.RegisterAgentAsync("a2", GenAgentPubKey(), Array.Empty<string>());
        await ca.RegisterAgentAsync("a3", GenAgentPubKey(), Array.Empty<string>());

        var all = await ca.ListAsync(revokedOnly: false);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task RegisterNode_UsesNodeEntityType()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        var frame = await ca.RegisterNodeAsync("node-1", GenAgentPubKey());
        Assert.Contains(":node:", frame.Nid);
    }

    [Fact]
    public async Task SerialNumbers_AreMonotonic_AcrossRegistrations()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        var f1 = await ca.RegisterAgentAsync("s1", GenAgentPubKey(), Array.Empty<string>());
        var f2 = await ca.RegisterAgentAsync("s2", GenAgentPubKey(), Array.Empty<string>());
        Assert.NotEqual(f1.Serial, f2.Serial);
        Assert.True(string.CompareOrdinal(f1.Serial, f2.Serial) < 0);
    }
}
