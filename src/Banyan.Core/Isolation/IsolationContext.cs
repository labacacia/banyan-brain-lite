// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Core.Isolation;

/// <summary>
/// The principal (NID identity) behind a request, as resolved from an NID
/// IdentFrame or a CA verify response. <see cref="Capabilities"/> mirrors the
/// capability strings issued by the NIP CA (e.g. <c>memory.read</c>).
/// </summary>
public sealed record PrincipalRef(
    string Nid,
    string? EntityType,
    IReadOnlySet<string> Capabilities)
{
    public static readonly PrincipalRef Anonymous =
        new(Nid: "anonymous", EntityType: null, Capabilities: new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Reference to a signed access envelope. Populated only by the enterprise
/// edition's isolation layer (which mirrors this contract locally);
/// <c>null</c> for Lite/Pro.
/// </summary>
public sealed record AccessEnvelopeRef(
    string EnvelopeId,
    string TenantId,
    bool SignatureVerified);

/// <summary>
/// The resolved isolation boundary for a single request. One layered model
/// across editions — each edition only populates the layers it supports:
/// <list type="bullet">
///   <item>Lite — <see cref="Namespace"/> + <see cref="PoolScopes"/>; tenant/org/workspace are placeholders.</item>
///   <item>Pro  — adds <see cref="OrgId"/>/<see cref="WorkspaceId"/> derived from the OrgNid.</item>
///   <item>Ent  — full chain; <see cref="TenantId"/> comes from a verified <see cref="Envelope"/>.</item>
/// </list>
/// </summary>
public sealed record IsolationContext(
    string TenantId,
    string OrgId,
    string WorkspaceId,
    string Namespace,
    IReadOnlyList<string> PoolScopes,
    PrincipalRef Principal,
    IReadOnlySet<string> Capabilities,
    AccessEnvelopeRef? Envelope = null)
{
    /// <summary>Local single-tenant context used by Lite and as the Pro/Ent placeholder for unused layers.</summary>
    public static IsolationContext Local(
        string @namespace,
        PrincipalRef principal,
        IReadOnlyList<string>? poolScopes = null)
        => new(
            TenantId: IsolationDefaults.LocalTenant,
            OrgId: IsolationDefaults.LocalOrg,
            WorkspaceId: IsolationDefaults.DefaultWorkspace,
            Namespace: @namespace,
            PoolScopes: poolScopes ?? Array.Empty<string>(),
            Principal: principal,
            Capabilities: principal.Capabilities);

    /// <summary>The namespaces this context may read: its own namespace plus any readable pool namespaces.</summary>
    public IReadOnlyList<string> ReadableNamespaces()
    {
        if (PoolScopes.Count == 0)
            return new[] { Namespace };
        var set = new List<string>(PoolScopes.Count + 1) { Namespace };
        foreach (var p in PoolScopes)
            if (!set.Contains(p, StringComparer.Ordinal))
                set.Add(p);
        return set;
    }
}

/// <summary>Placeholder identifiers for layers an edition does not use.</summary>
public static class IsolationDefaults
{
    public const string LocalTenant = "_local";
    public const string LocalOrg = "_local";
    public const string DefaultWorkspace = "default";
    public const string DefaultNamespace = "default";
}

/// <summary>Capability strings checked at the isolation boundary. Aligned with NIP CA issuance.</summary>
public static class IsolationCapabilities
{
    public const string MemoryRead = "memory.read";
    public const string MemoryWrite = "memory.write";
    public const string MemorySearch = "memory.search";
}
