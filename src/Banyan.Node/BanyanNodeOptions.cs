namespace Banyan.Node;

/// <summary>
/// Banyan-side configuration for <c>banyan serve</c>. Wraps the bits we own (paths, urls, CA
/// passphrase resolution) without colliding with NPS.NWP's <c>MemoryNodeOptions</c>, which we
/// configure separately inside <see cref="MemoryNodeApp"/>.
/// </summary>
public sealed class BanyanNodeOptions
{
    public string  Urls            { get; set; } = "http://localhost:17433";
    public string  MemoryDbPath    { get; set; } = "~/.banyan/memory.db";
    public string? SqliteVecLibPath{ get; set; }

    public string  NipCaDbPath     { get; set; } = "~/.banyan/nipca.db";
    public string  NipCaKeyPath    { get; set; } = "~/.banyan/nipca-key.pem";
    public string  CaNid           { get; set; } = "urn:nps:ca:local.banyan:root";

    /// <summary>NID → public key mappings the NIP verifier accepts. The in-process CA, when loaded, is added automatically.</summary>
    public Dictionary<string, string> TrustedIssuers { get; set; } = new();

    /// <summary>Identifier the node advertises in <c>/.nwm</c>.</summary>
    public string  NodeId          { get; set; } = "banyan-memory-node";
    public string  DisplayName     { get; set; } = "Banyan Memory Node";

    /// <summary>If true, NWP middleware accepts requests without IdentFrame (read-only).</summary>
    public bool    RequireAuth     { get; set; } = true;

    /// <summary>How aggressively the Banyan NID middleware enforces IdentFrame on /api/* and /v1/* routes. Default: AnonymousAllowed.</summary>
    public Banyan.Auth.NidAuthMode NidAuthMode { get; set; } = Banyan.Auth.NidAuthMode.AnonymousAllowed;

    public uint    DefaultLimit    { get; set; } = 20;
    public uint    MaxLimit        { get; set; } = 200;
    public uint    DefaultTokenBudget { get; set; } = 8192;
}
