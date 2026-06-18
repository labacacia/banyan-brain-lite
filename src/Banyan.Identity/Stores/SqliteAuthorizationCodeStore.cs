// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using OLS.Root.Oidc.Models;
using OLS.Root.Oidc.Stores;

namespace Banyan.Identity.Stores;

public sealed class SqliteAuthorizationCodeStore : IAuthorizationCodeStore
{
    private readonly SqliteConnection _conn;

    public SqliteAuthorizationCodeStore(SqliteConnection conn) => _conn = conn;

    public void Dispose() { }

    public Task StoreAsync(AuthorizationCode code, CancellationToken ct)
        => ExecAsync("""
            INSERT INTO ols_authorization_codes
                (code, client_id, subject_id, redirect_uri, code_challenge, code_challenge_method,
                 nonce, state, scopes_csv, created_at, expires_at)
            VALUES
                (@code, @cid, @sub, @ru, @cc, @ccm, @nonce, @state, @sc, @c, @e)
            """,
            ("@code",  code.Code),
            ("@cid",   code.ClientId),
            ("@sub",   code.SubjectId),
            ("@ru",    N(code.RedirectUri)),
            ("@cc",    N(code.CodeChallenge)),
            ("@ccm",   N(code.CodeChallengeMethod)),
            ("@nonce", N(code.Nonce)),
            ("@state", N(code.State)),
            ("@sc",    string.Join(',', code.RequestedScopes)),
            ("@c",     code.CreatedAt.ToString("O")),
            ("@e",     code.ExpiresAt.ToString("O")));

    public async Task<AuthorizationCode?> ConsumeAsync(string code, CancellationToken ct)
    {
        using var tx = _conn.BeginTransaction();
        AuthorizationCode? found;

        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT code, client_id, subject_id, redirect_uri, code_challenge, code_challenge_method,
                       nonce, state, scopes_csv, created_at, expires_at
                FROM   ols_authorization_codes
                WHERE  code = @c
                """;
            cmd.Parameters.AddWithValue("@c", code);
            using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) { tx.Commit(); return null; }
            found = new AuthorizationCode
            {
                Code                  = r.GetString(0),
                ClientId              = r.GetString(1),
                SubjectId             = r.GetString(2),
                RedirectUri           = r.IsDBNull(3) ? null! : r.GetString(3),
                CodeChallenge         = r.IsDBNull(4) ? null! : r.GetString(4),
                CodeChallengeMethod   = r.IsDBNull(5) ? null! : r.GetString(5),
                Nonce                 = r.IsDBNull(6) ? null! : r.GetString(6),
                State                 = r.IsDBNull(7) ? null! : r.GetString(7),
                RequestedScopes       = r.IsDBNull(8)
                    ? Array.Empty<string>()
                    : r.GetString(8).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                CreatedAt             = DateTimeOffset.Parse(r.GetString(9)),
                ExpiresAt             = DateTimeOffset.Parse(r.GetString(10)),
            };
        }

        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM ols_authorization_codes WHERE code = @c";
            del.Parameters.AddWithValue("@c", code);
            await del.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return found;
    }

    private async Task ExecAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
