namespace Banyan.MemoryScopes;

public sealed record MemoryScopeContext(
    string TenantId = "default",
    string WorkspaceId = "default",
    string? AgentId = null,
    string? SessionId = null);
