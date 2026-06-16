// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Banyan.Core;
using Microsoft.Data.Sqlite;

namespace Banyan.Lite;

/// <summary>
/// SQLite-backed tamper-evident audit log for Lite (OBS-4). Persists the
/// <see cref="AuditChain"/> hash chain so write/update/forget operations are
/// recorded with <c>(actor, action, target, result, ts)</c> and the whole log
/// can be verified offline. Appends are serialized so the chain stays linear.
/// </summary>
public sealed class SqliteAuditLog : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteAuditLog(SqliteConnection conn) => _conn = conn;

    public static async Task<SqliteAuditLog> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_log(
                seq       INTEGER PRIMARY KEY,
                ts        TEXT NOT NULL,
                actor     TEXT NOT NULL,
                action    TEXT NOT NULL,
                target    TEXT NOT NULL,
                result    TEXT NOT NULL,
                metadata  TEXT,
                prev_hash TEXT NOT NULL,
                hash      TEXT NOT NULL,
                signature TEXT)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        return new SqliteAuditLog(conn);
    }

    /// <summary>Appends one tamper-evident record, linking it to the prior hash.</summary>
    public async Task<AuditEntry> AppendAsync(
        string actor, string action, string target, string result,
        string? metadata = null, DateTimeOffset? timestamp = null,
        IAuditSigner? signer = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var prev = await ReadLastAsync(ct);
            var entry = AuditChain.AppendEntry(
                prev, timestamp ?? DateTimeOffset.UtcNow, actor, action, target, result, metadata, signer);

            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO audit_log(seq, ts, actor, action, target, result, metadata, prev_hash, hash, signature)
                VALUES(@seq, @ts, @actor, @action, @target, @result, @metadata, @prev, @hash, @sig)
                """;
            cmd.Parameters.AddWithValue("@seq", entry.Seq);
            cmd.Parameters.AddWithValue("@ts", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@actor", entry.Actor);
            cmd.Parameters.AddWithValue("@action", entry.Action);
            cmd.Parameters.AddWithValue("@target", entry.Target);
            cmd.Parameters.AddWithValue("@result", entry.Result);
            cmd.Parameters.AddWithValue("@metadata", (object?)entry.Metadata ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prev", entry.PrevHash);
            cmd.Parameters.AddWithValue("@hash", entry.Hash);
            cmd.Parameters.AddWithValue("@sig", (object?)entry.Signature ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        var entries = new List<AuditEntry>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seq, ts, actor, action, target, result, metadata, prev_hash, hash, signature FROM audit_log ORDER BY seq ASC";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            entries.Add(Map(r));
        return entries;
    }

    /// <summary>Verifies the persisted chain (linkage + recomputed hashes, and signatures when a signer is given).</summary>
    public async Task<AuditVerifyResult> VerifyAsync(IAuditSigner? signer = null, CancellationToken ct = default)
        => AuditChain.Verify(await ReadAllAsync(ct), signer);

    private async Task<AuditEntry?> ReadLastAsync(CancellationToken ct)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seq, ts, actor, action, target, result, metadata, prev_hash, hash, signature FROM audit_log ORDER BY seq DESC LIMIT 1";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static AuditEntry Map(SqliteDataReader r) => new(
        Seq: r.GetInt64(0),
        Timestamp: DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Actor: r.GetString(2),
        Action: r.GetString(3),
        Target: r.GetString(4),
        Result: r.GetString(5),
        Metadata: r.IsDBNull(6) ? null : r.GetString(6),
        PrevHash: r.GetString(7),
        Hash: r.GetString(8),
        Signature: r.IsDBNull(9) ? null : r.GetString(9));

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _conn.DisposeAsync();
    }
}
