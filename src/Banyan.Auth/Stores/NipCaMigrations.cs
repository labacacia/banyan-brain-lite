// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;

namespace Banyan.Auth.Stores;

internal static class NipCaMigrations
{
    private static readonly (int Version, string Sql)[] s_migrations =
    [
        (1, Migration001_Initial),
        (2, Migration002_Lineage),
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

            foreach (var stmt in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                await ExecAsync(conn, stmt);

            await ExecAsync(conn,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (@v, @t)",
                ("@v", version),
                ("@t", DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] p)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql, params (string Name, object Value)[] p)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    private const string Migration001_Initial = """
        CREATE TABLE nip_certs (
            nid             TEXT PRIMARY KEY,
            serial          TEXT NOT NULL UNIQUE,
            entity_type     TEXT NOT NULL,
            pub_key         TEXT NOT NULL,
            capabilities    TEXT NOT NULL,
            scope_json      TEXT,
            metadata_json   TEXT,
            issued_by       TEXT NOT NULL,
            issued_at       TEXT NOT NULL,
            expires_at      TEXT NOT NULL,
            revoked_at      TEXT,
            revoke_reason   TEXT
        );
        CREATE INDEX ix_nip_certs_serial   ON nip_certs(serial);
        CREATE INDEX ix_nip_certs_revoked  ON nip_certs(revoked_at) WHERE revoked_at IS NOT NULL;

        CREATE TABLE nip_serial (
            id   INTEGER PRIMARY KEY,
            next INTEGER NOT NULL DEFAULT 1
        );
        INSERT INTO nip_serial (id, next) VALUES (1, 1)
        """;

    private const string Migration002_Lineage = """
        ALTER TABLE nip_certs ADD COLUMN nid_role TEXT;
        ALTER TABLE nip_certs ADD COLUMN parent_nid TEXT;
        ALTER TABLE nip_certs ADD COLUMN lineage_json TEXT;
        CREATE INDEX ix_nip_certs_parent_nid ON nip_certs(parent_nid)
        """;
}
