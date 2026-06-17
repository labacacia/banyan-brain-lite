using Banyan.Auth;

namespace Banyan.Web;

/// <summary>
/// Configuration for the demo web app. All paths support a leading <c>~</c>.
/// CA passphrase comes from <c>BANYAN_NIP_CA_PASSPHRASE</c> at startup, or from a
/// local Lite-only secret file when the embedded CA is auto-initialised.
/// </summary>
public sealed class WebOptions
{
    public string  Urls           { get; set; } = "http://localhost:5180";
    public string  MemoryDbPath   { get; set; } = "~/.banyan/memory.db";
    public string  NipCaDbPath    { get; set; } = "~/.banyan/nipca.db";
    public string  NipCaKeyPath   { get; set; } = "~/.banyan/nipca-key.pem";
    public string  NipCaPassphrasePath { get; set; } = "~/.banyan/nipca-passphrase";
    public string  CaNid          { get; set; } = "urn:nps:ca:local.banyan:root";
    public CaServerMode CaServerType { get; set; } = CaServerMode.Embedded;
    public string? ExternalCaServerAddress { get; set; }
    public string  TokensCachePath{ get; set; } = "~/.banyan/tokens.json";
    public bool    OpenCa         { get; set; } = true;
    public string  LocalAgentId   { get; set; } = "banyan-lite-local";
    public string  LocalAgentProfilePath { get; set; } = "~/.banyan/agents/banyan-lite-local.json";
    public string  LocalAgentBrandPath { get; set; } = "~/.banyan/agents/banyan-lite-local-brand.json";

    // ── Human identity (OLS-backed OIDC + JWT) ──────────────────────────────────────
    // The web app creates these on first launch when missing, then forces browser admin setup.
    public string  IdentityDbPath         { get; set; } = "~/.banyan/identity.db";
    public string  IdentitySigningKeyPath { get; set; } = "~/.banyan/identity-signing.pem";
    public string  Audience               { get; set; } = "banyan";
    public TimeSpan AccessTokenExpiry     { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenExpiry    { get; set; } = TimeSpan.FromDays(30);
    public string  CliClientId            { get; set; } = "banyan-cli";

    /// <summary>Directory where uploaded .banyanpack files are stored.</summary>
    public string PackStorePath    { get; set; } = "~/.banyan/knowledge-packs/store";

    /// <summary>Path to the pack mount registry JSON file.</summary>
    public string PackRegistryPath { get; set; } = "~/.banyan/knowledge-packs/mounts.json";

    /// <summary>KB-3: when true, packs may only be mounted if signed by a publisher NID
    /// the CA trust chain vouches for (requires a CA). Default false (lenient).</summary>
    public bool RequirePackSignature { get; set; }

    /// <summary>Path to the sqlite-vec loadable extension. Null = auto-discover (env var / default cache).</summary>
    public string? SqliteVecLibPath { get; set; }

    /// <summary>How aggressively to enforce IdentFrame auth on /api/* and /v1/* routes. Default: AnonymousAllowed (Lite demo).</summary>
    public Banyan.Auth.NidAuthMode NidAuthMode { get; set; } = Banyan.Auth.NidAuthMode.AnonymousAllowed;

    /// <summary>
    /// External CA trust anchors when running without the embedded CA (--no-ca).
    /// Keys are CA NIDs; values are Ed25519 public keys in "ed25519:&lt;base64&gt;" form.
    /// Populated by repeated --trusted-issuer NID=PUBKEY flags on the CLI.
    /// </summary>
    public Dictionary<string, string> TrustedIssuers { get; set; } = new();

    /// <summary>
    /// OCSP endpoint of an external CA for online revocation checks.
    /// Null (default) disables OCSP — revocation is not checked.
    /// </summary>
    public string? ExternalOcspUrl { get; set; }

    // ── Authentication mode ───────────────────────────────────────────────
    /// <summary>
    /// <see cref="BanyanAuthMode.Local"/>: local OLS admin account + embedded CA (default).
    /// <see cref="BanyanAuthMode.Hub"/>: Hub-issued JWT for web UI; Hub NID for agents.
    /// </summary>
    public BanyanAuthMode AuthMode { get; set; } = BanyanAuthMode.Local;

    /// <summary>Hub IAM connection details. Only used when <see cref="AuthMode"/> is <see cref="BanyanAuthMode.Hub"/>.</summary>
    public HubAuthOptions Hub { get; set; } = new();

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
