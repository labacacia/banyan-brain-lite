using Banyan.Auth;

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

    // ── Act node (NPS-2) ──────────────────────────────────────────────────
    /// <summary>
    /// When true, an NWP Action Node is mounted at <c>/api/act</c> alongside the Memory Node.
    /// Exposes <c>memory.recall</c>, <c>memory.remember</c>, <c>memory.update</c>, <c>memory.forget</c>.
    /// </summary>
    public bool    EnableActNode   { get; set; } = true;

    // ── Authentication mode ───────────────────────────────────────────────
    /// <summary>
    /// <see cref="BanyanAuthMode.Offline"/>: all requests accepted without credentials (default).
    /// <see cref="BanyanAuthMode.Hub"/>: tokens and NID certs verified against the Hub IAM.
    /// </summary>
    public BanyanAuthMode AuthMode { get; set; } = BanyanAuthMode.Offline;

    /// <summary>Hub IAM connection details. Only used when <see cref="AuthMode"/> is <see cref="BanyanAuthMode.Hub"/>.</summary>
    public HubAuthOptions Hub { get; set; } = new();

    // ── MCP HTTP transport ────────────────────────────────────────────────
    /// <summary>
    /// When true, a Model Context Protocol (Streamable HTTP + SSE) endpoint is mounted
    /// at <see cref="McpPath"/>. Claude Desktop / Claude Code can connect via HTTP instead
    /// of spawning a stdio subprocess.
    /// </summary>
    public bool    EnableMcp       { get; set; } = true;
    /// <summary>Path at which the MCP endpoint is mounted. Default <c>/mcp</c>.</summary>
    public string  McpPath         { get; set; } = "/mcp";
    /// <summary>Default namespace used by the <c>memory.remember</c> MCP tool when the caller omits one.</summary>
    public string  McpDefaultNamespace { get; set; } = "default";
}
