// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Microsoft.Data.Sqlite;

namespace Banyan.Lite;

public static class LiteMemoryPool
{
    public static string Namespace(string poolId) => $"pool:{poolId}";
}

public sealed class SqliteMemoryPoolRepository : IMemoryPoolRepository
{
    private readonly SqliteConnection _conn;

    private SqliteMemoryPoolRepository(SqliteConnection conn) => _conn = conn;

    public static async Task<SqliteMemoryPoolRepository> OpenAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await Migrations.ApplyAsync(conn, ct);
        return new SqliteMemoryPoolRepository(conn);
    }

    public async Task<MemoryPool> CreateAsync(
        string name,
        string scope,
        string ownerId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Pool name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("Pool owner is required.", nameof(ownerId));

        var pool = new MemoryPool(
            Id: Guid.NewGuid().ToString("N"),
            Name: name.Trim(),
            Scope: MemoryPoolScopes.Normalize(scope),
            OwnerId: ownerId.Trim(),
            CreatedAt: DateTimeOffset.UtcNow);

        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_pools (id, name, scope, owner_id, created_at)
            VALUES (@id, @name, @scope, @owner, @created)
            """;
        cmd.Parameters.AddWithValue("@id", pool.Id);
        cmd.Parameters.AddWithValue("@name", pool.Name);
        cmd.Parameters.AddWithValue("@scope", pool.Scope);
        cmd.Parameters.AddWithValue("@owner", pool.OwnerId);
        cmd.Parameters.AddWithValue("@created", pool.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        await AddMemberAsync(pool.Id, pool.OwnerId, "owner", ct);
        return pool;
    }

    public async Task<IReadOnlyList<MemoryPool>> ListAsync(CancellationToken ct = default)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, scope, owner_id, created_at
            FROM memory_pools
            ORDER BY created_at, name
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MemoryPool>();
        while (await r.ReadAsync(ct)) result.Add(ReadPool(r));
        return result;
    }

    public async Task<MemoryPool?> GetAsync(string poolIdOrName, CancellationToken ct = default)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, scope, owner_id, created_at
            FROM memory_pools
            WHERE id = @id OR name = @id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", poolIdOrName);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPool(r) : null;
    }

    public async Task AddMemberAsync(
        string poolId,
        string memberId,
        string memberType,
        CancellationToken ct = default)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pool_memberships (pool_id, member_id, member_type, granted_at)
            VALUES (@pool, @member, @type, @granted)
            ON CONFLICT (pool_id, member_id) DO UPDATE SET
                member_type = excluded.member_type,
                granted_at = excluded.granted_at
            """;
        cmd.Parameters.AddWithValue("@pool", poolId);
        cmd.Parameters.AddWithValue("@member", memberId);
        cmd.Parameters.AddWithValue("@type", string.IsNullOrWhiteSpace(memberType) ? "agent" : memberType.Trim());
        cmd.Parameters.AddWithValue("@granted", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveMemberAsync(string poolId, string memberId, CancellationToken ct = default)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pool_memberships WHERE pool_id = @pool AND member_id = @member";
        cmd.Parameters.AddWithValue("@pool", poolId);
        cmd.Parameters.AddWithValue("@member", memberId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task BindAgentAsync(
        string agentId,
        string poolId,
        int priority = 100,
        CancellationToken ct = default)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memory_pool_bindings (agent_id, pool_id, priority, bound_at)
            VALUES (@agent, @pool, @priority, @bound)
            ON CONFLICT (agent_id, pool_id) DO UPDATE SET
                priority = excluded.priority,
                bound_at = excluded.bound_at
            """;
        cmd.Parameters.AddWithValue("@agent", agentId);
        cmd.Parameters.AddWithValue("@pool", poolId);
        cmd.Parameters.AddWithValue("@priority", priority);
        cmd.Parameters.AddWithValue("@bound", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryPoolBinding>> ListBindingsAsync(
        string agentId,
        CancellationToken ct = default)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, pool_id, priority, bound_at
            FROM memory_pool_bindings
            WHERE agent_id = @agent
            ORDER BY priority, bound_at
            """;
        cmd.Parameters.AddWithValue("@agent", agentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<MemoryPoolBinding>();
        while (await r.ReadAsync(ct))
            result.Add(new MemoryPoolBinding(
                r.GetString(0),
                r.GetString(1),
                r.GetInt32(2),
                DateTimeOffset.Parse(r.GetString(3))));
        return result;
    }

    public ValueTask DisposeAsync()
    {
        _conn.Dispose();
        return ValueTask.CompletedTask;
    }

    private static MemoryPool ReadPool(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        DateTimeOffset.Parse(r.GetString(4)));
}
