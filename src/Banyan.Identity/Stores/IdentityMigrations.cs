using Microsoft.Data.Sqlite;

namespace Banyan.Identity.Stores;

internal static class IdentityMigrations
{
    private static readonly (int Version, string Sql)[] s_migrations =
    [
        (1, Migration001_Identity),
    ];

    public static async Task ApplyAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        await ExecAsync(conn, "PRAGMA journal_mode=WAL");
        await ExecAsync(conn, "PRAGMA foreign_keys=ON");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version    INTEGER PRIMARY KEY,
                applied_at TEXT    NOT NULL
            )
            """);

        foreach (var (version, sql) in s_migrations)
        {
            var exists = Convert.ToInt64(
                await ScalarAsync(conn, "SELECT COUNT(1) FROM schema_migrations WHERE version=@v",
                    ("@v", version))) > 0;
            if (exists) continue;

            foreach (var stmt in Split(sql))
                await ExecAsync(conn, stmt);

            await ExecAsync(conn,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (@v, @t)",
                ("@v", version),
                ("@t", DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    private static IEnumerable<string> Split(string sql) =>
        sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task ExecAsync(
        SqliteConnection conn, string sql,
        params (string Name, object Value)[] p)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection conn, string sql,
        params (string Name, object Value)[] p)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    private const string Migration001_Identity = """
        CREATE TABLE ols_users (
            id                       TEXT PRIMARY KEY,
            user_name                TEXT,
            normalized_user_name     TEXT UNIQUE,
            email                    TEXT,
            normalized_email         TEXT,
            email_confirmed          INTEGER NOT NULL DEFAULT 0,
            password_hash            TEXT,
            security_stamp           TEXT,
            concurrency_stamp        TEXT,
            phone_number             TEXT,
            phone_number_confirmed   INTEGER NOT NULL DEFAULT 0,
            two_factor_enabled       INTEGER NOT NULL DEFAULT 0,
            lockout_end              TEXT,
            lockout_enabled          INTEGER NOT NULL DEFAULT 1,
            access_failed_count      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX ix_ols_users_email ON ols_users(normalized_email);

        CREATE TABLE ols_roles (
            id                 TEXT PRIMARY KEY,
            name               TEXT,
            normalized_name    TEXT UNIQUE,
            concurrency_stamp  TEXT
        );

        CREATE TABLE ols_user_roles (
            user_id  TEXT NOT NULL REFERENCES ols_users(id) ON DELETE CASCADE,
            role_id  TEXT NOT NULL REFERENCES ols_roles(id) ON DELETE CASCADE,
            PRIMARY KEY (user_id, role_id)
        );

        CREATE TABLE ols_refresh_tokens (
            id                     TEXT PRIMARY KEY,
            user_id                TEXT NOT NULL REFERENCES ols_users(id) ON DELETE CASCADE,
            token_hash             TEXT NOT NULL UNIQUE,
            created_at             TEXT NOT NULL,
            expires_at             TEXT NOT NULL,
            is_revoked             INTEGER NOT NULL DEFAULT 0,
            is_active              INTEGER NOT NULL DEFAULT 1,
            replaced_by_token_id   TEXT
        );
        CREATE INDEX ix_ols_rt_user ON ols_refresh_tokens(user_id);

        CREATE TABLE ols_oidc_clients (
            client_id                          TEXT PRIMARY KEY,
            client_name                        TEXT,
            is_enabled                         INTEGER NOT NULL DEFAULT 1,
            require_client_secret              INTEGER NOT NULL DEFAULT 1,
            require_pkce                       INTEGER NOT NULL DEFAULT 1,
            sliding_refresh_token_expiry       INTEGER NOT NULL DEFAULT 1,
            access_token_lifetime_sec          INTEGER NOT NULL,
            authorization_code_lifetime_sec    INTEGER NOT NULL,
            refresh_token_lifetime_sec         INTEGER NOT NULL
        );

        CREATE TABLE ols_oidc_client_secrets (
            client_id      TEXT NOT NULL REFERENCES ols_oidc_clients(client_id) ON DELETE CASCADE,
            hashed_secret  TEXT NOT NULL,
            PRIMARY KEY (client_id, hashed_secret)
        );

        CREATE TABLE ols_oidc_client_strings (
            client_id  TEXT NOT NULL REFERENCES ols_oidc_clients(client_id) ON DELETE CASCADE,
            kind       TEXT NOT NULL,
            value      TEXT NOT NULL,
            PRIMARY KEY (client_id, kind, value)
        );

        CREATE TABLE ols_authorization_codes (
            code                    TEXT PRIMARY KEY,
            client_id               TEXT NOT NULL,
            subject_id              TEXT NOT NULL,
            redirect_uri            TEXT,
            code_challenge          TEXT,
            code_challenge_method   TEXT,
            nonce                   TEXT,
            state                   TEXT,
            scopes_csv              TEXT,
            created_at              TEXT NOT NULL,
            expires_at              TEXT NOT NULL
        );

        CREATE TABLE ols_device_codes (
            code             TEXT PRIMARY KEY,
            user_code        TEXT NOT NULL UNIQUE,
            client_id        TEXT NOT NULL,
            subject_id       TEXT,
            scopes_csv       TEXT,
            is_authorized    INTEGER NOT NULL DEFAULT 0,
            is_denied        INTEGER NOT NULL DEFAULT 0,
            last_polled_at   TEXT,
            interval_sec     INTEGER NOT NULL,
            created_at       TEXT NOT NULL,
            expires_at       TEXT NOT NULL
        );

        CREATE TABLE ols_reference_tokens (
            token_hash    TEXT PRIMARY KEY,
            subject_id    TEXT,
            client_id     TEXT,
            scopes        TEXT,
            created_at    TEXT NOT NULL,
            expires_at    TEXT NOT NULL,
            is_revoked    INTEGER NOT NULL DEFAULT 0,
            is_active     INTEGER NOT NULL DEFAULT 1
        )
        """;
}
