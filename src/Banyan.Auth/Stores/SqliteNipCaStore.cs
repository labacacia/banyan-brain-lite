// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.Data.Sqlite;
using NPS.NIP.Ca;

namespace Banyan.Auth.Stores;

/// <summary>
/// SQLite-backed <see cref="INipCaStore"/> for embedded Banyan deployments.
/// Replaces the official <c>PostgreSqlNipCaStore</c> to avoid a Postgres dependency.
/// Serial numbers are zero-padded 16-char lowercase hex of a monotonic counter (e.g. <c>0000000000000001</c>).
/// </summary>
public sealed class SqliteNipCaStore : INipCaStore, IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly bool _ownsConnection;

    private SqliteNipCaStore(SqliteConnection conn, bool ownsConnection)
    {
        _conn = conn;
        _ownsConnection = ownsConnection;
    }

    public static async Task<SqliteNipCaStore> OpenAsync(string connectionString, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await NipCaMigrations.ApplyAsync(conn, ct);
        return new SqliteNipCaStore(conn, ownsConnection: true);
    }

    public static SqliteNipCaStore Open(string connectionString)
        => OpenAsync(connectionString).GetAwaiter().GetResult();

    public static Task<SqliteNipCaStore> OpenInMemoryAsync(CancellationToken ct = default)
        => OpenAsync("Data Source=:memory:", ct);

    // ── INipCaStore ───────────────────────────────────────────────────────────

    public async Task<NipCertRecord?> GetByNidAsync(string nid, CancellationToken ct)
        => await FindOneAsync("nid = @v", nid, ct);

    public async Task<NipCertRecord?> GetBySerialAsync(string serial, CancellationToken ct)
        => await FindOneAsync("serial = @v", serial, ct);

    public async Task<IReadOnlyList<NipCertRecord>> GetByParentNidAsync(string parentNid, CancellationToken ct)
    {
        var list = new List<NipCertRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} FROM nip_certs WHERE parent_nid = @parent ORDER BY issued_at DESC";
        cmd.Parameters.AddWithValue("@parent", parentNid);
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<IReadOnlyList<NipCertRecord>> GetRevokedAsync(CancellationToken ct)
    {
        var list = new List<NipCertRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} FROM nip_certs WHERE revoked_at IS NOT NULL";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public async Task<string> NextSerialAsync(CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE nip_serial SET next = next + 1 WHERE id = 1 RETURNING next";
        var next = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return (next - 1).ToString("x16");
    }

    public async Task<bool> RevokeAsync(string nid, string reason, DateTime revokedAt, CancellationToken ct)
    {
        var rows = await ExecAsync("""
            UPDATE nip_certs
            SET revoked_at = @at, revoke_reason = @r
            WHERE nid = @nid AND revoked_at IS NULL
            """,
            ("@nid", nid),
            ("@at",  ToIso(revokedAt)),
            ("@r",   N(reason)));
        return rows > 0;
    }

    public async Task SaveAsync(NipCertRecord record, CancellationToken ct)
    {
        await ExecAsync("""
            INSERT INTO nip_certs
                (nid, serial, entity_type, pub_key, capabilities, scope_json, metadata_json,
                 issued_by, issued_at, expires_at, revoked_at, revoke_reason,
                 nid_role, parent_nid, lineage_json)
            VALUES
                (@nid, @serial, @et, @pk, @caps, @scope, @meta,
                 @issuer, @ia, @ea, @ra, @rr,
                 @nidRole, @parentNid, @lineage)
            ON CONFLICT (nid) DO UPDATE SET
                serial        = excluded.serial,
                entity_type   = excluded.entity_type,
                pub_key       = excluded.pub_key,
                capabilities  = excluded.capabilities,
                scope_json    = excluded.scope_json,
                metadata_json = excluded.metadata_json,
                issued_by     = excluded.issued_by,
                issued_at     = excluded.issued_at,
                expires_at    = excluded.expires_at,
                revoked_at    = excluded.revoked_at,
                revoke_reason = excluded.revoke_reason,
                nid_role      = excluded.nid_role,
                parent_nid    = excluded.parent_nid,
                lineage_json  = excluded.lineage_json
            """,
            ("@nid",    record.Nid),
            ("@serial", record.Serial),
            ("@et",     record.EntityType),
            ("@pk",     record.PubKey),
            ("@caps",   JsonSerializer.Serialize(record.Capabilities ?? Array.Empty<string>())),
            ("@scope",  N(record.ScopeJson)),
            ("@meta",   N(record.MetadataJson)),
            ("@issuer", record.IssuedBy),
            ("@ia",     ToIso(record.IssuedAt)),
            ("@ea",     ToIso(record.ExpiresAt)),
            ("@ra",     N(record.RevokedAt is { } rdt ? ToIso(rdt) : null)),
            ("@rr",     N(record.RevokeReason)),
            ("@nidRole", N(record.NidRole)),
            ("@parentNid", N(record.ParentNid)),
            ("@lineage", N(record.LineageJson)));
    }

    /// <summary>Convenience for CLI: list all certs, optionally revoked-only.</summary>
    public async Task<IReadOnlyList<NipCertRecord>> ListAsync(bool revokedOnly, CancellationToken ct)
    {
        var list = new List<NipCertRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = revokedOnly
            ? $"{SelectColumns} FROM nip_certs WHERE revoked_at IS NOT NULL ORDER BY issued_at DESC"
            : $"{SelectColumns} FROM nip_certs ORDER BY issued_at DESC";
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    public void Dispose()
    {
        if (_ownsConnection) _conn.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsConnection) _conn.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string SelectColumns =
        "SELECT nid, serial, entity_type, pub_key, capabilities, scope_json, metadata_json, " +
        "issued_by, issued_at, expires_at, revoked_at, revoke_reason, " +
        "nid_role, parent_nid, lineage_json";

    private async Task<NipCertRecord?> FindOneAsync(string whereClause, string value, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} FROM nip_certs WHERE {whereClause}";
        cmd.Parameters.AddWithValue("@v", value);
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    private static NipCertRecord Read(SqliteDataReader r)
    {
        var caps = JsonSerializer.Deserialize<string[]>(r.GetString(4)) ?? Array.Empty<string>();
        return new NipCertRecord
        {
            Nid           = r.GetString(0),
            Serial        = r.GetString(1),
            EntityType    = r.GetString(2),
            PubKey        = r.GetString(3),
            Capabilities  = caps,
            ScopeJson     = r.IsDBNull(5) ? null! : r.GetString(5),
            MetadataJson  = r.IsDBNull(6) ? null! : r.GetString(6),
            IssuedBy      = r.GetString(7),
            IssuedAt      = DateTime.Parse(r.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            ExpiresAt     = DateTime.Parse(r.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            RevokedAt     = r.IsDBNull(10) ? null : DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
            RevokeReason  = r.IsDBNull(11) ? null! : r.GetString(11),
            NidRole       = r.IsDBNull(12) ? null! : r.GetString(12),
            ParentNid     = r.IsDBNull(13) ? null! : r.GetString(13),
            LineageJson   = r.IsDBNull(14) ? null! : r.GetString(14),
        };
    }

    private async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static string ToIso(DateTime dt) => dt.ToUniversalTime().ToString("O");
    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
