using Microsoft.Data.Sqlite;

namespace Banyan.Lite;

internal static class Migrations
{
    // Each entry: (version, sql). Statements separated by semicolons.
    private static readonly (int Version, string Sql)[] s_migrations =
    [
        (1, Migration001_Initial),
        (2, Migration002_Embeddings),
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

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    // ── Migration 001: Core tables ────────────────────────────────────────────

    private const string Migration001_Initial = """
        CREATE TABLE namespaces (
            namespace  TEXT NOT NULL PRIMARY KEY,
            created_at TEXT NOT NULL
        );
        INSERT OR IGNORE INTO namespaces VALUES ('default', datetime('now'));
        CREATE TABLE memory_events (
            event_id    TEXT    NOT NULL PRIMARY KEY,
            memory_id   TEXT    NOT NULL,
            type        INTEGER NOT NULL,
            content     TEXT,
            metadata    TEXT,
            agent_nid   TEXT,
            namespace   TEXT    NOT NULL DEFAULT 'default',
            occurred_at TEXT    NOT NULL
        );
        CREATE INDEX ix_me_mid ON memory_events (memory_id, occurred_at);
        CREATE INDEX ix_me_ns  ON memory_events (namespace, occurred_at);
        CREATE TABLE memories_current (
            memory_id  TEXT NOT NULL PRIMARY KEY,
            event_id   TEXT NOT NULL,
            namespace  TEXT NOT NULL DEFAULT 'default',
            content    TEXT NOT NULL,
            metadata   TEXT,
            agent_nid  TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX ix_mc_ns ON memories_current (namespace, updated_at);
        CREATE VIRTUAL TABLE memories_fts USING fts5 (
            memory_id UNINDEXED,
            namespace UNINDEXED,
            content,
            tokenize = 'unicode61 remove_diacritics 1'
        )
        """;

    // ── Migration 002: embeddings table ──────────────────────────────────────
    // Vector storage as raw little-endian float32 BLOB. ANN search runs over the table
    // in application code (linear scan + cosine). Adequate for demo data sizes; swap for
    // sqlite-vec or HNSW once volumes grow.

    private const string Migration002_Embeddings = """
        CREATE TABLE embeddings (
            memory_id  TEXT    NOT NULL PRIMARY KEY REFERENCES memories_current(memory_id) ON DELETE CASCADE,
            namespace  TEXT    NOT NULL,
            model_id   TEXT    NOT NULL,
            dim        INTEGER NOT NULL,
            vector     BLOB    NOT NULL,
            updated_at TEXT    NOT NULL
        );
        CREATE INDEX ix_embeddings_ns ON embeddings (namespace)
        """;
}
