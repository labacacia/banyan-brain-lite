// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using InnoLotus.Root.Core.Models;
using InnoLotus.Root.Core.Results;
using InnoLotus.Root.Core.Stores;

namespace Banyan.Identity.Stores;

public sealed class SqliteRoleStore : IRoleStore<IdentityRole>
{
    private readonly SqliteConnection _conn;

    public SqliteRoleStore(SqliteConnection conn) => _conn = conn;

    public void Dispose() { }

    // ── Persistence ───────────────────────────────────────────────────────────

    public async Task<IdentityResult> CreateAsync(IdentityRole role, CancellationToken ct)
    {
        role.Id ??= Guid.NewGuid().ToString("D");
        role.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        try
        {
            await ExecAsync(
                "INSERT INTO ols_roles (id, name, normalized_name, concurrency_stamp) VALUES (@id, @n, @nn, @cs)",
                ("@id", role.Id),
                ("@n",  N(role.Name)),
                ("@nn", N(role.NormalizedName)),
                ("@cs", role.ConcurrencyStamp));
            return IdentityResult.Success;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return IdentityResult.Failed(IdentityErrors.DuplicateRoleName(role.Name ?? ""));
        }
    }

    public async Task<IdentityResult> UpdateAsync(IdentityRole role, CancellationToken ct)
    {
        var fresh = Guid.NewGuid().ToString("N");
        var rows = await ExecAsync("""
            UPDATE ols_roles SET name = @n, normalized_name = @nn, concurrency_stamp = @new_cs
            WHERE id = @id AND concurrency_stamp = @old_cs
            """,
            ("@id",     role.Id),
            ("@n",      N(role.Name)),
            ("@nn",     N(role.NormalizedName)),
            ("@new_cs", fresh),
            ("@old_cs", N(role.ConcurrencyStamp)));
        if (rows == 0) return IdentityResult.Failed(IdentityErrors.ConcurrencyFailure());
        role.ConcurrencyStamp = fresh;
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(IdentityRole role, CancellationToken ct)
    {
        var rows = await ExecAsync(
            "DELETE FROM ols_roles WHERE id = @id AND concurrency_stamp = @cs",
            ("@id", role.Id),
            ("@cs", N(role.ConcurrencyStamp)));
        return rows == 0
            ? IdentityResult.Failed(IdentityErrors.ConcurrencyFailure())
            : IdentityResult.Success;
    }

    public Task<IdentityRole?> FindByIdAsync(string roleId, CancellationToken ct)
        => FindOneAsync("id = @v", roleId, ct);

    public Task<IdentityRole?> FindByNameAsync(string normalizedRoleName, CancellationToken ct)
        => FindOneAsync("normalized_name = @v", normalizedRoleName, ct);

    // ── In-memory accessors ───────────────────────────────────────────────────

    public Task<string> GetRoleIdAsync(IdentityRole role, CancellationToken ct)
        => Task.FromResult(role.Id);

    public Task<string?> GetRoleNameAsync(IdentityRole role, CancellationToken ct)
        => Task.FromResult<string?>(role.Name);

    public Task SetRoleNameAsync(IdentityRole role, string? roleName, CancellationToken ct)
    { role.Name = roleName!; return Task.CompletedTask; }

    public Task<string?> GetNormalizedRoleNameAsync(IdentityRole role, CancellationToken ct)
        => Task.FromResult<string?>(role.NormalizedName);

    public Task SetNormalizedRoleNameAsync(IdentityRole role, string? normalizedName, CancellationToken ct)
    { role.NormalizedName = normalizedName!; return Task.CompletedTask; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IdentityRole?> FindOneAsync(string whereClause, string value, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT id, name, normalized_name, concurrency_stamp FROM ols_roles WHERE {whereClause}";
        cmd.Parameters.AddWithValue("@v", value);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new IdentityRole
        {
            Id               = r.GetString(0),
            Name             = r.IsDBNull(1) ? null! : r.GetString(1),
            NormalizedName   = r.IsDBNull(2) ? null! : r.GetString(2),
            ConcurrencyStamp = r.IsDBNull(3) ? null! : r.GetString(3),
        };
    }

    private async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
