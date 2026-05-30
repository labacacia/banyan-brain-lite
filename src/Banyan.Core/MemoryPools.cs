namespace Banyan.Core;

public static class MemoryPoolScopes
{
    public const string Personal = "personal";
    public const string LocalWorkspace = "local_workspace";
    public const string AgentSession = "agent_session";

    public static readonly string[] All = [Personal, LocalWorkspace, AgentSession];

    public static string Normalize(string scope)
        => scope.Trim().ToLowerInvariant() switch
        {
            "workspace" => LocalWorkspace,
            "agent" => AgentSession,
            var value when All.Contains(value, StringComparer.Ordinal) => value,
            _ => throw new ArgumentException(
                $"Invalid pool scope '{scope}'. Expected personal, local_workspace, or agent_session.",
                nameof(scope)),
        };
}

public sealed record MemoryPool(
    string Id,
    string Name,
    string Scope,
    string OwnerId,
    DateTimeOffset CreatedAt);

public sealed record MemoryPoolMembership(
    string PoolId,
    string MemberId,
    string MemberType,
    DateTimeOffset GrantedAt);

public sealed record MemoryPoolBinding(
    string AgentId,
    string PoolId,
    int Priority,
    DateTimeOffset BoundAt);

public interface IMemoryPoolRepository : IAsyncDisposable
{
    Task<MemoryPool> CreateAsync(string name, string scope, string ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryPool>> ListAsync(CancellationToken ct = default);
    Task<MemoryPool?> GetAsync(string poolIdOrName, CancellationToken ct = default);
    Task AddMemberAsync(string poolId, string memberId, string memberType, CancellationToken ct = default);
    Task RemoveMemberAsync(string poolId, string memberId, CancellationToken ct = default);
    Task BindAgentAsync(string agentId, string poolId, int priority = 100, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryPoolBinding>> ListBindingsAsync(string agentId, CancellationToken ct = default);
}
