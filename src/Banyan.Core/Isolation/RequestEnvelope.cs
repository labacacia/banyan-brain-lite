namespace Banyan.Core.Isolation;

/// <summary>
/// Transport-neutral inputs an <see cref="IIsolationContextResolver"/> needs to
/// resolve an <see cref="IsolationContext"/>. Each transport (HTTP, NPS, MCP,
/// CLI) populates this before the handler runs; handlers never parse identity or
/// scope themselves. Edition-specific resolvers read only the fields they need.
/// </summary>
public sealed record RequestEnvelope(
    /// <summary>Raw <c>Authorization</c> credential (e.g. <c>NID base64(IdentFrame)</c>); null when anonymous.</summary>
    string? Authorization,
    /// <summary>Logical transport name, for diagnostics and parity tests: http | nps | grpc | mcp | a2a | cli.</summary>
    string Transport = "http",
    /// <summary>Namespace the caller is targeting, if explicitly supplied.</summary>
    string? RequestedNamespace = null,
    /// <summary>Pool namespaces/ids the caller asked to include in scope.</summary>
    IReadOnlyList<string>? RequestedPools = null,
    /// <summary>Tenant/org/workspace declared in the payload — validated against the resolved context to block cross-boundary spoofing.</summary>
    string? DeclaredTenant = null,
    string? DeclaredOrg = null,
    string? DeclaredWorkspace = null);
