using Banyan.Identity.Stores;
using Microsoft.Data.Sqlite;

namespace Banyan.Identity;

/// <summary>
/// Facade that owns the identity <see cref="SqliteConnection"/> and exposes
/// all 7 OLS-compatible stores. Mirrors the <c>SqliteMemoryStore</c> pattern.
/// </summary>
public sealed class SqliteIdentityStore : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _conn;

    public SqliteUserStore                Users               { get; }
    public SqliteRoleStore                Roles               { get; }
    public SqliteRefreshTokenStore        RefreshTokens       { get; }
    public SqliteOidcClientStore          OidcClients         { get; }
    public SqliteAuthorizationCodeStore   AuthorizationCodes  { get; }
    public SqliteDeviceCodeStore          DeviceCodes         { get; }
    public SqliteReferenceTokenStore      ReferenceTokens     { get; }

    private SqliteIdentityStore(SqliteConnection conn)
    {
        _conn               = conn;
        Users               = new SqliteUserStore(conn);
        Roles               = new SqliteRoleStore(conn);
        RefreshTokens       = new SqliteRefreshTokenStore(conn);
        OidcClients         = new SqliteOidcClientStore(conn);
        AuthorizationCodes  = new SqliteAuthorizationCodeStore(conn);
        DeviceCodes         = new SqliteDeviceCodeStore(conn);
        ReferenceTokens     = new SqliteReferenceTokenStore(conn);
    }

    public static async Task<SqliteIdentityStore> OpenAsync(
        string connectionString, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await IdentityMigrations.ApplyAsync(conn, ct);
        return new SqliteIdentityStore(conn);
    }

    /// <summary>Synchronous variant for DI factories where async-await is awkward.</summary>
    public static SqliteIdentityStore Open(string connectionString)
        => OpenAsync(connectionString).GetAwaiter().GetResult();

    public static Task<SqliteIdentityStore> OpenInMemoryAsync(CancellationToken ct = default)
        => OpenAsync("Data Source=:memory:", ct);

    public async Task<bool> HasUsersInRoleAsync(string normalizedRoleName, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1)
            FROM ols_user_roles ur
            JOIN ols_roles r ON r.id = ur.role_id
            WHERE r.normalized_name = @role
            """;
        cmd.Parameters.AddWithValue("@role", normalizedRoleName);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return count > 0;
    }

    public void Dispose() => _conn.Dispose();

    public ValueTask DisposeAsync()
    {
        _conn.Dispose();
        return ValueTask.CompletedTask;
    }
}
