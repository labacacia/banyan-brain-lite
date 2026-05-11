using Microsoft.Data.Sqlite;
using OLS.Root.Oidc.Models;
using OLS.Root.Oidc.Stores;

namespace Banyan.Identity.Stores;

public sealed class SqliteDeviceCodeStore : IDeviceCodeStore
{
    private readonly SqliteConnection _conn;

    public SqliteDeviceCodeStore(SqliteConnection conn) => _conn = conn;

    public void Dispose() { }

    public Task StoreAsync(DeviceCode deviceCode, CancellationToken ct)
        => ExecAsync("""
            INSERT INTO ols_device_codes
                (code, user_code, client_id, subject_id, scopes_csv,
                 is_authorized, is_denied, last_polled_at, interval_sec, created_at, expires_at)
            VALUES
                (@code, @uc, @cid, @sub, @sc, @auth, @den, @poll, @iv, @c, @e)
            """,
            ("@code", deviceCode.Code),
            ("@uc",   deviceCode.UserCode),
            ("@cid",  deviceCode.ClientId),
            ("@sub",  N(deviceCode.SubjectId)),
            ("@sc",   string.Join(',', deviceCode.RequestedScopes)),
            ("@auth", deviceCode.IsAuthorized ? 1 : 0),
            ("@den",  deviceCode.IsDenied ? 1 : 0),
            ("@poll", N(deviceCode.LastPolledAt?.ToString("O"))),
            ("@iv",   deviceCode.Interval),
            ("@c",    deviceCode.CreatedAt.ToString("O")),
            ("@e",    deviceCode.ExpiresAt.ToString("O")));

    public Task UpdateAsync(DeviceCode deviceCode, CancellationToken ct)
        => ExecAsync("""
            UPDATE ols_device_codes SET
                subject_id     = @sub,
                is_authorized  = @auth,
                is_denied      = @den,
                last_polled_at = @poll
            WHERE code = @code
            """,
            ("@code", deviceCode.Code),
            ("@sub",  N(deviceCode.SubjectId)),
            ("@auth", deviceCode.IsAuthorized ? 1 : 0),
            ("@den",  deviceCode.IsDenied ? 1 : 0),
            ("@poll", N(deviceCode.LastPolledAt?.ToString("O"))));

    public Task<DeviceCode?> FindByDeviceCodeAsync(string deviceCode, CancellationToken ct)
        => FindOneAsync("code = @v", deviceCode, ct);

    public Task<DeviceCode?> FindByUserCodeAsync(string userCode, CancellationToken ct)
        => FindOneAsync("user_code = @v", userCode, ct);

    public Task RemoveAsync(string deviceCode, CancellationToken ct)
        => ExecAsync("DELETE FROM ols_device_codes WHERE code = @c", ("@c", deviceCode));

    private async Task<DeviceCode?> FindOneAsync(string whereClause, string value, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT code, user_code, client_id, subject_id, scopes_csv,
                   is_authorized, is_denied, last_polled_at, interval_sec, created_at, expires_at
            FROM   ols_device_codes
            WHERE  {whereClause}
            """;
        cmd.Parameters.AddWithValue("@v", value);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new DeviceCode
        {
            Code             = r.GetString(0),
            UserCode         = r.GetString(1),
            ClientId         = r.GetString(2),
            SubjectId        = r.IsDBNull(3) ? null! : r.GetString(3),
            RequestedScopes  = r.IsDBNull(4)
                ? Array.Empty<string>()
                : r.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IsAuthorized     = r.GetInt64(5) != 0,
            IsDenied         = r.GetInt64(6) != 0,
            LastPolledAt     = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
            Interval         = (int)r.GetInt64(8),
            CreatedAt        = DateTimeOffset.Parse(r.GetString(9)),
            ExpiresAt        = DateTimeOffset.Parse(r.GetString(10)),
        };
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
