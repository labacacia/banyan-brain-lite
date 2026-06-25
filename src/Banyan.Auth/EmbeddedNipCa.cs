// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth.Stores;
using NPS.NIP.Ca;
using NPS.NIP.Crypto;
using NPS.NIP.Frames;

namespace Banyan.Auth;

/// <summary>
/// In-process NID Certificate Authority backed by <see cref="SqliteNipCaStore"/> and <see cref="NipKeyManager"/>.
/// Wraps the upstream <see cref="NipCaService"/> so callers don't have to assemble its three dependencies by hand.
/// </summary>
public sealed class EmbeddedNipCa : IAsyncDisposable
{
    private readonly SqliteNipCaStore _store;
    private readonly NipKeyManager    _keys;

    public NipCaService Service  { get; }
    public NipCaOptions Options  { get; }
    public string       CaNid    => Options.CaNid;
    public string       CaPubKey => Service.GetCaPublicKey();

    private EmbeddedNipCa(NipCaService service, NipCaOptions options, SqliteNipCaStore store, NipKeyManager keys)
    {
        Service  = service;
        Options  = options;
        _store   = store;
        _keys    = keys;
    }

    /// <summary>
    /// Open (or create) an embedded CA. The DB file is migrated on first call.
    /// The CA private key at <see cref="BanyanNipCaOptions.KeyFilePath"/> must already exist
    /// (use <see cref="GenerateKey"/> first if not).
    /// </summary>
    public static async Task<EmbeddedNipCa> OpenAsync(BanyanNipCaOptions banyanOpts, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(banyanOpts.KeyPassphrase))
            throw new InvalidOperationException("BanyanNipCaOptions.KeyPassphrase is required.");

        var dbPath  = ExpandHome(banyanOpts.DbPath);
        var keyPath = ExpandHome(banyanOpts.KeyFilePath);
        if (!File.Exists(keyPath))
            throw new FileNotFoundException(
                $"CA private key not found at {keyPath}. Run `banyan ca init` first or call EmbeddedNipCa.GenerateKey().",
                keyPath);

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var store = await SqliteNipCaStore.OpenAsync($"Data Source={dbPath}", ct);
        var keys  = new NipKeyManager();
        keys.Load(keyPath, banyanOpts.KeyPassphrase);

        var npsOpts = ToNpsOptions(banyanOpts);
        var service = new NipCaService(npsOpts, store, keys);
        return new EmbeddedNipCa(service, npsOpts, store, keys);
    }

    /// <summary>Synchronous variant for non-async call sites (CLI bootstrap).</summary>
    public static EmbeddedNipCa Open(BanyanNipCaOptions opts) => OpenAsync(opts).GetAwaiter().GetResult();

    /// <summary>Generate a fresh CA private key at <see cref="BanyanNipCaOptions.KeyFilePath"/>. Refuses to overwrite an existing file.</summary>
    public static void GenerateKey(BanyanNipCaOptions opts, bool overwrite = false)
    {
        if (string.IsNullOrEmpty(opts.KeyPassphrase))
            throw new InvalidOperationException("BanyanNipCaOptions.KeyPassphrase is required.");

        var path = ExpandHome(opts.KeyFilePath);
        if (File.Exists(path) && !overwrite)
            throw new IOException($"Refusing to overwrite existing CA key at {path}.");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var keys = new NipKeyManager();
        keys.Generate(path, opts.KeyPassphrase);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ── Convenience helpers (delegate straight to NipCaService) ───────────────

    public Task<IdentFrame>  RegisterAgentAsync(string identifier, string pubKey, IReadOnlyList<string> capabilities, CancellationToken ct = default)
        => Service.RegisterAsync("agent", identifier, pubKey, capabilities, "{}", "{}", ct);

    public Task<IdentFrame>  RegisterNodeAsync (string identifier, string pubKey, CancellationToken ct = default)
        => Service.RegisterAsync("node",  identifier, pubKey, Array.Empty<string>(), "{}", "{}", ct);

    public Task<IdentFrame>  RenewAsync (string nid, CancellationToken ct = default) => Service.RenewAsync(nid, ct);
    public Task<RevokeFrame> RevokeAsync(string nid, string reason, CancellationToken ct = default) => Service.RevokeAsync(nid, reason, ct);
    public Task<NipVerifyResult> VerifyAsync(string nid, CancellationToken ct = default) => Service.VerifyAsync(nid, ct);

    /// <summary>List all certs the CA has issued (CLI-friendly).</summary>
    public Task<IReadOnlyList<NipCertRecord>> ListAsync(bool revokedOnly = false, CancellationToken ct = default)
        => _store.ListAsync(revokedOnly, ct);

    public async ValueTask DisposeAsync()
    {
        _keys.Dispose();
        await _store.DisposeAsync();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static NipCaOptions ToNpsOptions(BanyanNipCaOptions o) => new()
    {
        BaseUrl                    = o.BaseUrl,
        CaNid                      = o.CaNid,
        ConnectionString           = "",                          // unused — we supply the store directly
        DisplayName                = o.DisplayName,
        KeyFilePath                = ExpandHome(o.KeyFilePath),
        KeyPassphrase              = o.KeyPassphrase,
        AgentCertValidityDays      = o.AgentCertValidityDays,
        NodeCertValidityDays       = o.NodeCertValidityDays,
        RenewalWindowDays          = o.RenewalWindowDays,
        Algorithms                 = new[] { "ed25519" },
        RoutePrefix                = "",   // CA routes mount at root /v1/... (NipCaRouter.MapNipCa)
        NormalizeOcspResponseTime  = false,
    };

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
