// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Core.Isolation;

/// <summary>
/// The single enforcement seam every data access goes through. The
/// <c>ScopedMemoryStore</c> decorator (ISO-3) calls these on every
/// Search/Count/Remember/Update/Forget so no transport or edition reimplements
/// the boundary.
/// </summary>
public interface IIsolationEnforcer
{
    /// <summary>Throws <see cref="IsolationDeniedException"/> if the context lacks <paramref name="capability"/>.</summary>
    void RequireCapability(IsolationContext ctx, string capability);

    /// <summary>Returns a copy of <paramref name="query"/> restricted to namespaces the context may read.</summary>
    SearchQuery ApplyScope(IsolationContext ctx, SearchQuery query);

    /// <summary>Throws if a write targets a namespace outside the context's boundary.</summary>
    void AssertNamespaceWritable(IsolationContext ctx, string targetNamespace);

    /// <summary>Throws if a namespace is not within the context's readable set (own namespace + readable pools).</summary>
    void AssertNamespaceReadable(IsolationContext ctx, string targetNamespace);

    /// <summary>
    /// Throws if any tenant/org/workspace declared on the payload disagrees with the resolved
    /// context (cross-boundary spoof guard). Lite/Pro pass the layers they use; Ent also checks tenant.
    /// </summary>
    void AssertNoCrossBoundary(
        IsolationContext ctx,
        string? declaredTenant = null,
        string? declaredOrg = null,
        string? declaredWorkspace = null);
}

/// <summary>Raised when a request violates the isolation boundary. Maps to HTTP 403 at the transport edge.</summary>
public sealed class IsolationDeniedException : Exception
{
    public string Reason { get; }

    public IsolationDeniedException(string reason)
        : base($"Isolation denied: {reason}")
        => Reason = reason;
}
