using Microsoft.Data.Sqlite;
using OLS.Root.Oidc.Stores;

namespace Banyan.Identity.Stores;

public sealed class SqliteReferenceTokenStore : IReferenceTokenStore
{
    private readonly SqliteConnection _conn;

    public SqliteReferenceTokenStore(SqliteConnection conn) => _conn = conn;

    public void Dispose() { }

    public Task StoreAsync(StoredToken token, CancellationToken ct)
        => ExecAsync("""
            INSERT INTO ols_reference_tokens
                (token_hash, subject_id, client_id, scopes, created_at, expires_at, is_revoked, is_active)
            VALUES
                (@h, @sub, @cid, @sc, @c, @e, @r, @a)
            """,
            ("@h",   token.TokenHash),
            ("@sub", N(token.SubjectId)),
            ("@cid", N(token.ClientId)),
            ("@sc",  N(token.Scopes)),
            ("@c",   token.CreatedAt.ToString("O")),
            ("@e",   token.ExpiresAt.ToString("O")),
            ("@r",   token.IsRevoked ? 1 : 0),
            ("@a",   token.IsActive  ? 1 : 0));

    public async Task<StoredToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT token_hash, subject_id, client_id, scopes, created_at, expires_at, is_revoked, is_active
            FROM   ols_reference_tokens
            WHERE  token_hash = @h
            """;
        cmd.Parameters.AddWithValue("@h", tokenHash);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new StoredToken
        {
            TokenHash   = r.GetString(0),
            SubjectId   = r.IsDBNull(1) ? null! : r.GetString(1),
            ClientId    = r.IsDBNull(2) ? null! : r.GetString(2),
            Scopes      = r.IsDBNull(3) ? null! : r.GetString(3),
            CreatedAt   = DateTimeOffset.Parse(r.GetString(4)),
            ExpiresAt   = DateTimeOffset.Parse(r.GetString(5)),
            IsRevoked   = r.GetInt64(6) != 0,
        };
    }

    public Task RevokeAsync(string tokenHash, CancellationToken ct)
        => ExecAsync(
            "UPDATE ols_reference_tokens SET is_revoked = 1, is_active = 0 WHERE token_hash = @h",
            ("@h", tokenHash));

    public Task RevokeAllAsync(string subjectId, string clientId, CancellationToken ct)
        => ExecAsync("""
            UPDATE ols_reference_tokens
            SET is_revoked = 1, is_active = 0
            WHERE subject_id = @sub AND client_id = @cid AND is_active = 1
            """,
            ("@sub", subjectId),
            ("@cid", clientId));

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
