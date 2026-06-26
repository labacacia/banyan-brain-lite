// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Banyan.Auth;
using NPS.Conformance;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;
using NPS.NIP.Verification;
using NSec.Cryptography;
using Xunit;

namespace Banyan.Auth.Tests;

/// <summary>
/// NPS Node-Profile L1 conformance for Banyan Lite's <b>identity</b> track (NIP).
/// <para>
/// Banyan Lite's <see cref="EmbeddedNipCa"/> + the SDK <see cref="NipIdentVerifier"/> are the
/// implementation-under-test for the NIP slice of <c>NPS-Node-L1</c>. This is a <b>scoped</b>
/// attestation: it covers the NIP cases the CA/identity track is responsible for
/// (TC-N1-NIP-01/02/03); the NCP cases are attested on the Ent native-transport side, and
/// NDP/NWP/OBS cases are out of scope here. We therefore assert each covered case individually
/// against the official <see cref="NpsConformanceCatalog"/> rather than running the
/// full-profile <see cref="NpsConformanceValidator"/> (which requires every NodeL1 case).
/// </para>
/// </summary>
public sealed class NipConformanceTests : IDisposable
{
    // Scoped subset of NPS-Node-L1 this suite is the IUT for.
    public const string CaseKeypair    = "TC-N1-NIP-01"; // Root keypair generation and permission
    public const string CaseSignVerify = "TC-N1-NIP-02"; // IdentFrame sign and verify
    public const string CaseNidFormat  = "TC-N1-NIP-03"; // NID format

    private static readonly string[] Scope = [CaseKeypair, CaseSignVerify, CaseNidFormat];

    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-nip-conf-" + Guid.NewGuid().ToString("N")[..8]);
    private const string  Passphrase = "conf-pass-2026";

    public NipConformanceTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose()         { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    private BanyanNipCaOptions Opts() => new()
    {
        DbPath        = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N")[..8] + ".db"),
        KeyFilePath   = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N")[..8] + "-ca.pem"),
        KeyPassphrase = Passphrase,
        CaNid         = "urn:nps:ca:test.banyan:root",
        BaseUrl       = "http://localhost:0",
    };

    private static string GenAgentPubKey()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return NipSigner.EncodePublicKey(key.PublicKey);
    }

    // ── Behavioral checks (one per covered case) ────────────────────────────────

    // TC-N1-NIP-01 — the CA root key is generated as an encrypted PEM and, on POSIX, is
    // readable/writable only by the owner (0600). Mirrors EmbeddedNipCa.GenerateKey.
    private void Check_Keypair()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);

        Assert.True(File.Exists(opts.KeyFilePath));
        Assert.NotEmpty(File.ReadAllText(opts.KeyFilePath));

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var mode = File.GetUnixFileMode(opts.KeyFilePath);
            var groupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                           | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal(UnixFileMode.None, mode & groupOther);
        }
    }

    // TC-N1-NIP-02 — a CA-issued IdentFrame carries a signature that the SDK NipIdentVerifier
    // (NPS-3 §7) accepts against the issuing CA's trust anchor, and rejects under an empty anchor set.
    private async Task Check_SignVerify()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        IdentFrame ident = await ca.RegisterAgentAsync("alpha", GenAgentPubKey(), new[] { "memory.read" });
        Assert.NotEmpty(ident.Signature);

        var trusted = new NipIdentVerifier(new NipVerifierOptions
        {
            TrustedIssuers      = new Dictionary<string, string> { [ca.CaNid] = ca.CaPubKey },
            LocalRevokedSerials = new HashSet<string>(),
            OcspUrl             = null!,
        });
        var ok = await trusted.VerifyAsync(ident);
        Assert.True(ok.IsValid, $"expected IsValid; got step={ok.FailedStep} code={ok.ErrorCode} msg={ok.Message}");

        var untrusted = new NipIdentVerifier(new NipVerifierOptions
        {
            TrustedIssuers      = new Dictionary<string, string>(),
            LocalRevokedSerials = new HashSet<string>(),
            OcspUrl             = null!,
        });
        var rejected = await untrusted.VerifyAsync(ident);
        Assert.False(rejected.IsValid);
    }

    // TC-N1-NIP-03 — issued NIDs follow urn:nps:<type>:<authority>:<local-id>.
    private async Task Check_NidFormat()
    {
        var opts = Opts();
        EmbeddedNipCa.GenerateKey(opts);
        await using var ca = await EmbeddedNipCa.OpenAsync(opts);

        var agent = await ca.RegisterAgentAsync("alpha", GenAgentPubKey(), Array.Empty<string>());
        var node  = await ca.RegisterNodeAsync("node-1", GenAgentPubKey());

        foreach (var (nid, type) in new[] { (agent.Nid, "agent"), (node.Nid, "node") })
        {
            var parts = nid.Split(':');
            Assert.Equal(5, parts.Length);                         // urn : nps : <type> : <authority> : <local-id>
            Assert.Equal("urn", parts[0]);
            Assert.Equal("nps", parts[1]);
            Assert.Equal(type, parts[2]);
            Assert.NotEmpty(parts[3]);
            Assert.NotEmpty(parts[4]);
        }
    }

    private Task RunAsync(string caseId) => caseId switch
    {
        CaseKeypair    => Task.Run(Check_Keypair),
        CaseSignVerify => Check_SignVerify(),
        CaseNidFormat  => Check_NidFormat(),
        _              => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Not in scope."),
    };

    // ── Per-case facts (granular CI reporting) ──────────────────────────────────

    [Theory]
    [InlineData(CaseKeypair)]
    [InlineData(CaseSignVerify)]
    [InlineData(CaseNidFormat)]
    public async Task NodeL1_Nip_Case_Passes(string caseId)
    {
        // Each covered id must be a real, required (non-optional) NodeL1 catalog case.
        var entry = NpsConformanceCatalog.NodeL1.SingleOrDefault(c => c.Id == caseId);
        Assert.NotNull(entry);
        Assert.False(entry!.Optional, $"{caseId} is optional in the catalog; the scoped attestation expects a required case.");

        await RunAsync(caseId);
    }

    // ── Aggregate: emit a scoped NodeL1 manifest reflecting real outcomes ────────

    [Fact]
    public async Task Emits_Scoped_NodeL1_Nip_Manifest()
    {
        var results = new List<NpsConformanceCaseResult>();
        foreach (var id in Scope)
        {
            string outcome, message;
            try { await RunAsync(id); outcome = "pass"; message = NpsConformanceCatalog.NodeL1.Single(c => c.Id == id).Title; }
            catch (Exception ex) { outcome = "fail"; message = ex.Message; }
            results.Add(new NpsConformanceCaseResult { Id = id, Result = outcome, Message = message });
        }

        Assert.All(results, r => Assert.Equal("pass", r.Result));

        var manifest = NpsConformanceManifest.Create(
            profile:    NpsConformanceProfiles.NodeL1,
            iutName:    "Banyan.Lite.Nip",
            iutVersion: typeof(EmbeddedNipCa).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            iutNid:     "urn:nps:ca:local.banyan:root",
            peerName:   "NPS.NIP",
            peerVersion: typeof(NipIdentVerifier).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            results:    results,
            environment: "banyan-lite-auth-tests");

        // Round-trips through the official schema; the covered subset is exactly the declared scope.
        var json = manifest.ToJson();
        var back = NpsConformanceManifest.FromJson(json);
        Assert.Equal(Scope.Length, back.Cases.Count);
        Assert.Equal(Scope.Length, back.Summary.Pass);
        Assert.Equal(NpsConformanceProfiles.NodeL1, back.Profile);

        var outDir = Path.Combine(AppContext.BaseDirectory, "conformance");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "nps-node-l1-nip.banyan-lite.json"), json);
    }
}
