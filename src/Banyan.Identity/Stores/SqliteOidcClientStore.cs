using Microsoft.Data.Sqlite;
using OLS.Root.Oidc.Models;
using OLS.Root.Oidc.Stores;

namespace Banyan.Identity.Stores;

public sealed class SqliteOidcClientStore : IClientStore
{
    private readonly SqliteConnection _conn;

    public SqliteOidcClientStore(SqliteConnection conn) => _conn = conn;

    public void Dispose() { }

    public async Task<OidcClient?> FindByClientIdAsync(string clientId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT client_id, client_name, is_enabled, require_client_secret, require_pkce,
                   sliding_refresh_token_expiry,
                   access_token_lifetime_sec, authorization_code_lifetime_sec, refresh_token_lifetime_sec
            FROM   ols_oidc_clients
            WHERE  client_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", clientId);

        OidcClient? client;
        using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            client = new OidcClient
            {
                ClientId                    = r.GetString(0),
                ClientName                  = r.IsDBNull(1) ? null! : r.GetString(1),
                IsEnabled                   = r.GetInt64(2) != 0,
                RequireClientSecret         = r.GetInt64(3) != 0,
                RequirePkce                 = r.GetInt64(4) != 0,
                SlidingRefreshTokenExpiry   = r.GetInt64(5) != 0,
                AccessTokenLifetime         = TimeSpan.FromSeconds(r.GetInt64(6)),
                AuthorizationCodeLifetime   = TimeSpan.FromSeconds(r.GetInt64(7)),
                RefreshTokenLifetime        = TimeSpan.FromSeconds(r.GetInt64(8)),
            };
        }

        client.HashedSecrets        = await ListAsync("SELECT hashed_secret FROM ols_oidc_client_secrets WHERE client_id = @id", clientId, ct);
        client.RedirectUris         = await ListByKindAsync(clientId, "redirect", ct);
        client.PostLogoutRedirectUris = await ListByKindAsync(clientId, "post_logout", ct);
        client.AllowedCorsOrigins   = await ListByKindAsync(clientId, "cors", ct);
        client.AllowedScopes        = await ListByKindAsync(clientId, "scope", ct);
        client.AllowedGrantTypes    = await ListByKindAsync(clientId, "grant", ct);
        return client;
    }

    /// <summary>Upsert a client and replace its child rows. Used by <c>banyan init</c> to seed the CLI client.</summary>
    public async Task UpsertAsync(OidcClient client, CancellationToken ct = default)
    {
        using var tx = _conn.BeginTransaction();

        Exec(tx, """
            INSERT INTO ols_oidc_clients
                (client_id, client_name, is_enabled, require_client_secret, require_pkce,
                 sliding_refresh_token_expiry,
                 access_token_lifetime_sec, authorization_code_lifetime_sec, refresh_token_lifetime_sec)
            VALUES (@id, @n, @en, @rcs, @pkce, @slide, @atl, @acl, @rtl)
            ON CONFLICT (client_id) DO UPDATE SET
                client_name                     = excluded.client_name,
                is_enabled                      = excluded.is_enabled,
                require_client_secret           = excluded.require_client_secret,
                require_pkce                    = excluded.require_pkce,
                sliding_refresh_token_expiry    = excluded.sliding_refresh_token_expiry,
                access_token_lifetime_sec       = excluded.access_token_lifetime_sec,
                authorization_code_lifetime_sec = excluded.authorization_code_lifetime_sec,
                refresh_token_lifetime_sec      = excluded.refresh_token_lifetime_sec
            """,
            ("@id",    client.ClientId),
            ("@n",     N(client.ClientName)),
            ("@en",    client.IsEnabled ? 1 : 0),
            ("@rcs",   client.RequireClientSecret ? 1 : 0),
            ("@pkce",  client.RequirePkce ? 1 : 0),
            ("@slide", client.SlidingRefreshTokenExpiry ? 1 : 0),
            ("@atl",   (long)client.AccessTokenLifetime.TotalSeconds),
            ("@acl",   (long)client.AuthorizationCodeLifetime.TotalSeconds),
            ("@rtl",   (long)client.RefreshTokenLifetime.TotalSeconds));

        Exec(tx, "DELETE FROM ols_oidc_client_secrets WHERE client_id = @id", ("@id", client.ClientId));
        foreach (var s in client.HashedSecrets)
            Exec(tx,
                "INSERT INTO ols_oidc_client_secrets (client_id, hashed_secret) VALUES (@id, @s)",
                ("@id", client.ClientId), ("@s", s));

        Exec(tx, "DELETE FROM ols_oidc_client_strings WHERE client_id = @id", ("@id", client.ClientId));
        InsertStrings(tx, client.ClientId, "redirect",    client.RedirectUris);
        InsertStrings(tx, client.ClientId, "post_logout", client.PostLogoutRedirectUris);
        InsertStrings(tx, client.ClientId, "cors",        client.AllowedCorsOrigins);
        InsertStrings(tx, client.ClientId, "scope",       client.AllowedScopes);
        InsertStrings(tx, client.ClientId, "grant",       client.AllowedGrantTypes);

        tx.Commit();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IList<string>> ListByKindAsync(string clientId, string kind, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM ols_oidc_client_strings WHERE client_id = @id AND kind = @k";
        cmd.Parameters.AddWithValue("@id", clientId);
        cmd.Parameters.AddWithValue("@k",  kind);
        var list = new List<string>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private async Task<IList<string>> ListAsync(string sql, string clientId, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", clientId);
        var list = new List<string>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private static void InsertStrings(SqliteTransaction tx, string clientId, string kind, IList<string> values)
    {
        foreach (var v in values)
            Exec(tx,
                "INSERT INTO ols_oidc_client_strings (client_id, kind, value) VALUES (@id, @k, @v) ON CONFLICT DO NOTHING",
                ("@id", clientId), ("@k", kind), ("@v", v));
    }

    private static void Exec(SqliteTransaction tx, string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
