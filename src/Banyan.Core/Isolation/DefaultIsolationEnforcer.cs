namespace Banyan.Core.Isolation;

/// <summary>
/// Pure, infrastructure-free implementation of <see cref="IIsolationEnforcer"/>.
/// Shared by Lite and Pro; Ent mirrors this logic over its local contracts (ISO-2).
/// </summary>
public sealed class DefaultIsolationEnforcer : IIsolationEnforcer
{
    public void RequireCapability(IsolationContext ctx, string capability)
    {
        if (!ctx.Capabilities.Contains(capability))
            throw new IsolationDeniedException($"missing capability '{capability}'");
    }

    public SearchQuery ApplyScope(IsolationContext ctx, SearchQuery query)
    {
        var allowed = ctx.ReadableNamespaces();

        // Narrow any caller-requested namespaces to the allowed set; never widen.
        IReadOnlyList<string> scoped;
        if (query.Namespaces is { Count: > 0 } requested)
        {
            scoped = requested.Where(n => allowed.Contains(n, StringComparer.Ordinal)).ToArray();
            if (scoped.Count == 0)
                throw new IsolationDeniedException("requested namespaces are outside the isolation boundary");
        }
        else if (query.Namespace is { Length: > 0 } single)
        {
            if (!allowed.Contains(single, StringComparer.Ordinal))
                throw new IsolationDeniedException($"namespace '{single}' is outside the isolation boundary");
            scoped = new[] { single };
        }
        else
        {
            scoped = allowed;
        }

        return query with
        {
            Namespace = scoped.Count == 1 ? scoped[0] : null,
            Namespaces = scoped,
        };
    }

    public void AssertNamespaceWritable(IsolationContext ctx, string targetNamespace)
    {
        // Writes must land in the context's own namespace, never a pool or another scope.
        if (!string.Equals(targetNamespace, ctx.Namespace, StringComparison.Ordinal))
            throw new IsolationDeniedException(
                $"write to namespace '{targetNamespace}' outside boundary '{ctx.Namespace}'");
    }

    public void AssertNamespaceReadable(IsolationContext ctx, string targetNamespace)
    {
        if (!ctx.ReadableNamespaces().Contains(targetNamespace, StringComparer.Ordinal))
            throw new IsolationDeniedException(
                $"namespace '{targetNamespace}' is outside the readable boundary");
    }

    public void AssertNoCrossBoundary(
        IsolationContext ctx,
        string? declaredTenant = null,
        string? declaredOrg = null,
        string? declaredWorkspace = null)
    {
        if (declaredTenant is not null && !string.Equals(declaredTenant, ctx.TenantId, StringComparison.Ordinal))
            throw new IsolationDeniedException("declared tenant does not match resolved context");
        if (declaredOrg is not null && !string.Equals(declaredOrg, ctx.OrgId, StringComparison.Ordinal))
            throw new IsolationDeniedException("declared organization does not match resolved context");
        if (declaredWorkspace is not null && !string.Equals(declaredWorkspace, ctx.WorkspaceId, StringComparison.Ordinal))
            throw new IsolationDeniedException("declared workspace does not match resolved context");
    }
}
