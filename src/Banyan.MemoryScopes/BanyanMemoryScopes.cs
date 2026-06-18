// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.MemoryScopes;

public static class BanyanMemoryScopes
{
    public static string Tenant(MemoryScopeContext context)
        => $"tenant:{Normalize(context.TenantId, "default")}";

    public static string Workspace(MemoryScopeContext context)
        => $"workspace:{Normalize(context.TenantId, "default")}:{Normalize(context.WorkspaceId, "default")}";

    public static string? Agent(MemoryScopeContext context)
        => string.IsNullOrWhiteSpace(context.AgentId)
            ? null
            : $"agent:{Normalize(context.TenantId, "default")}:{Normalize(context.WorkspaceId, "default")}:{context.AgentId}";

    public static string? Session(MemoryScopeContext context)
        => string.IsNullOrWhiteSpace(context.AgentId) || string.IsNullOrWhiteSpace(context.SessionId)
            ? null
            : $"session:{Normalize(context.TenantId, "default")}:{Normalize(context.WorkspaceId, "default")}:{context.AgentId}:{context.SessionId}";

    public static IReadOnlyList<string> ReadableScopes(
        MemoryScopeContext context,
        params string?[] legacyScopes)
    {
        var scopes = new List<string>();
        Add(Session(context));
        Add(Agent(context));
        foreach (var legacy in legacyScopes) Add(legacy);
        Add(Workspace(context));
        Add(Tenant(context));
        return scopes;

        void Add(string? scope)
        {
            if (!string.IsNullOrWhiteSpace(scope) && !scopes.Contains(scope, StringComparer.Ordinal))
                scopes.Add(scope);
        }
    }

    public static string? ResolveAlias(MemoryScopeContext context, string requestedScope)
    {
        var scope = requestedScope.Trim();
        return scope.ToLowerInvariant() switch
        {
            "tenant" => Tenant(context),
            "workspace" => Workspace(context),
            "agent" => Agent(context),
            "session" => Session(context),
            _ => scope
        };
    }

    public static string? ResolveScopeLevel(MemoryScopeContext context, string? scopeLevel)
        => (scopeLevel ?? "workspace").Trim().ToLowerInvariant() switch
        {
            "tenant" => Tenant(context),
            "workspace" => Workspace(context),
            "agent" => Agent(context),
            "session" => Session(context),
            _ => null
        };

    public static bool CanAccess(MemoryScopeContext context, string scope, params string?[] legacyScopes)
        => ReadableScopes(context, legacyScopes).Contains(scope, StringComparer.Ordinal);

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
