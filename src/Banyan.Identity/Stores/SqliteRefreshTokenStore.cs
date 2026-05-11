using Microsoft.Data.Sqlite;
using OLS.Root.Authentication.Models;
using OLS.Root.Authentication.Stores;
using OLS.Root.Core.Models;

namespace Banyan.Identity.Stores;

public sealed class SqliteRefreshTokenStore : IRefreshTokenStore<IdentityUser>
{
    private readonly SqliteConnection _conn;

    public SqliteRefreshTokenStore(SqliteConnection conn) => _conn = conn;

    public void Dispose() { }

    public async Task CreateAsync(RefreshToken token, CancellationToken ct)
    {
        await ExecAsync("""
            INSERT INTO ols_refresh_tokens
                (id, user_id, token_hash, created_at, expires_at, is_revoked, is_active, replaced_by_token_id)
            VALUES
                (@id, @uid, @hash, @c, @e, @r, @a, @repl)
            """,
            ("@id",   token.Id),
            ("@uid",  token.UserId),
            ("@hash", token.TokenHash),
            ("@c",    token.CreatedAt.ToString("O")),
            ("@e",    token.ExpiresAt.ToString("O")),
            ("@r",    token.IsRevoked ? 1 : 0),
            ("@a",    token.IsActive  ? 1 : 0),
            ("@repl", N(token.ReplacedByTokenId)));
    }

    public async Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, user_id, token_hash, created_at, expires_at, is_revoked, is_active, replaced_by_token_id
            FROM   ols_refresh_tokens
            WHERE  token_hash = @h
            """;
        cmd.Parameters.AddWithValue("@h", tokenHash);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new RefreshToken
        {
            Id                 = r.GetString(0),
            UserId             = r.GetString(1),
            TokenHash          = r.GetString(2),
            CreatedAt          = DateTimeOffset.Parse(r.GetString(3)),
            ExpiresAt          = DateTimeOffset.Parse(r.GetString(4)),
            IsRevoked          = r.GetInt64(5) != 0,
            ReplacedByTokenId  = r.IsDBNull(7) ? null! : r.GetString(7),
        };
    }

    public Task RevokeAsync(string tokenId, string? replacedByTokenId, CancellationToken ct)
        => ExecAsync("""
            UPDATE ols_refresh_tokens
            SET is_revoked = 1, is_active = 0, replaced_by_token_id = @repl
            WHERE id = @id
            """,
            ("@id",   tokenId),
            ("@repl", N(replacedByTokenId)));

    public Task RevokeAllForUserAsync(string userId, CancellationToken ct)
        => ExecAsync("""
            UPDATE ols_refresh_tokens
            SET is_revoked = 1, is_active = 0
            WHERE user_id = @u AND is_active = 1
            """,
            ("@u", userId));

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
