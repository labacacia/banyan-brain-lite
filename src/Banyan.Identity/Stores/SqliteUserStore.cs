using Microsoft.Data.Sqlite;
using OLS.Root.Core.Models;
using OLS.Root.Core.Results;
using OLS.Root.Core.Stores;

namespace Banyan.Identity.Stores;

/// <summary>
/// SQLite-backed implementation of the OLS user-related stores.
/// Set* methods only mutate the in-memory <see cref="IdentityUser"/> object;
/// the DB is touched on Create / Update / Delete / Find* and on role-link operations.
/// Optimistic concurrency uses <c>concurrency_stamp</c>.
/// </summary>
public sealed class SqliteUserStore :
    IUserStore<IdentityUser>,
    IUserPasswordStore<IdentityUser>,
    IUserEmailStore<IdentityUser>,
    IUserLockoutStore<IdentityUser>,
    IUserRoleStore<IdentityUser>,
    IUserTwoFactorStore<IdentityUser>
{
    private readonly SqliteConnection _conn;

    public SqliteUserStore(SqliteConnection conn) => _conn = conn;

    /// <summary>No-op: the underlying <see cref="SqliteConnection"/> is owned by DI.</summary>
    public void Dispose() { }

    // ── IUserStore: persistence ───────────────────────────────────────────────

    public async Task<IdentityResult> CreateAsync(IdentityUser user, CancellationToken ct)
    {
        user.Id ??= Guid.NewGuid().ToString("D");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        try
        {
            await ExecAsync("""
                INSERT INTO ols_users
                    (id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                     password_hash, security_stamp, concurrency_stamp,
                     phone_number, phone_number_confirmed, two_factor_enabled,
                     lockout_end, lockout_enabled, access_failed_count)
                VALUES
                    (@id, @un, @nun, @em, @nem, @ec,
                     @ph, @ss, @cs,
                     @pn, @pnc, @tf,
                     @le, @lo, @afc)
                """,
                ("@id",  user.Id),
                ("@un",  N(user.UserName)),
                ("@nun", N(user.NormalizedUserName)),
                ("@em",  N(user.Email)),
                ("@nem", N(user.NormalizedEmail)),
                ("@ec",  user.EmailConfirmed ? 1 : 0),
                ("@ph",  N(user.PasswordHash)),
                ("@ss",  N(user.SecurityStamp)),
                ("@cs",  user.ConcurrencyStamp),
                ("@pn",  N(user.PhoneNumber)),
                ("@pnc", user.PhoneNumberConfirmed ? 1 : 0),
                ("@tf",  user.TwoFactorEnabled ? 1 : 0),
                ("@le",  N(user.LockoutEnd?.ToString("O"))),
                ("@lo",  user.LockoutEnabled ? 1 : 0),
                ("@afc", user.AccessFailedCount));
            return IdentityResult.Success;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            return IdentityResult.Failed(IdentityErrors.DuplicateUserName(user.UserName ?? ""));
        }
    }

    public async Task<IdentityResult> UpdateAsync(IdentityUser user, CancellationToken ct)
    {
        var old = user.ConcurrencyStamp;
        var fresh = Guid.NewGuid().ToString("N");

        var rows = await ExecAsync("""
            UPDATE ols_users SET
                user_name = @un, normalized_user_name = @nun,
                email = @em, normalized_email = @nem, email_confirmed = @ec,
                password_hash = @ph, security_stamp = @ss, concurrency_stamp = @new_cs,
                phone_number = @pn, phone_number_confirmed = @pnc, two_factor_enabled = @tf,
                lockout_end = @le, lockout_enabled = @lo, access_failed_count = @afc
            WHERE id = @id AND concurrency_stamp = @old_cs
            """,
            ("@id",     user.Id),
            ("@un",     N(user.UserName)),
            ("@nun",    N(user.NormalizedUserName)),
            ("@em",     N(user.Email)),
            ("@nem",    N(user.NormalizedEmail)),
            ("@ec",     user.EmailConfirmed ? 1 : 0),
            ("@ph",     N(user.PasswordHash)),
            ("@ss",     N(user.SecurityStamp)),
            ("@new_cs", fresh),
            ("@old_cs", N(old)),
            ("@pn",     N(user.PhoneNumber)),
            ("@pnc",    user.PhoneNumberConfirmed ? 1 : 0),
            ("@tf",     user.TwoFactorEnabled ? 1 : 0),
            ("@le",     N(user.LockoutEnd?.ToString("O"))),
            ("@lo",     user.LockoutEnabled ? 1 : 0),
            ("@afc",    user.AccessFailedCount));

        if (rows == 0) return IdentityResult.Failed(IdentityErrors.ConcurrencyFailure());
        user.ConcurrencyStamp = fresh;
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(IdentityUser user, CancellationToken ct)
    {
        var rows = await ExecAsync(
            "DELETE FROM ols_users WHERE id = @id AND concurrency_stamp = @cs",
            ("@id", user.Id),
            ("@cs", N(user.ConcurrencyStamp)));
        return rows == 0
            ? IdentityResult.Failed(IdentityErrors.ConcurrencyFailure())
            : IdentityResult.Success;
    }

    public Task<IdentityUser?> FindByIdAsync(string userId, CancellationToken ct)
        => FindOneAsync("id = @v", userId, ct);

    public Task<IdentityUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct)
        => FindOneAsync("normalized_user_name = @v", normalizedUserName, ct);

    public Task<IdentityUser?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
        => FindOneAsync("normalized_email = @v", normalizedEmail, ct);

    // ── IUserStore: in-memory accessors ───────────────────────────────────────

    public Task<string> GetUserIdAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult<string?>(user.UserName);

    public Task<string?> GetNormalizedUserNameAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult<string?>(user.NormalizedUserName);

    public Task SetUserNameAsync(IdentityUser user, string? userName, CancellationToken ct)
    { user.UserName = userName!; return Task.CompletedTask; }

    public Task SetNormalizedUserNameAsync(IdentityUser user, string? normalizedName, CancellationToken ct)
    { user.NormalizedUserName = normalizedName!; return Task.CompletedTask; }

    // ── IUserPasswordStore ────────────────────────────────────────────────────

    public Task SetPasswordHashAsync(IdentityUser user, string? passwordHash, CancellationToken ct)
    { user.PasswordHash = passwordHash!; return Task.CompletedTask; }

    public Task<string?> GetPasswordHashAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult<string?>(user.PasswordHash);

    public Task<bool> HasPasswordAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));

    // ── IUserEmailStore ───────────────────────────────────────────────────────

    public Task SetEmailAsync(IdentityUser user, string? email, CancellationToken ct)
    { user.Email = email!; return Task.CompletedTask; }

    public Task<string?> GetEmailAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult<string?>(user.Email);

    public Task<bool> GetEmailConfirmedAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(IdentityUser user, bool confirmed, CancellationToken ct)
    { user.EmailConfirmed = confirmed; return Task.CompletedTask; }

    public Task<string?> GetNormalizedEmailAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult<string?>(user.NormalizedEmail);

    public Task SetNormalizedEmailAsync(IdentityUser user, string? normalizedEmail, CancellationToken ct)
    { user.NormalizedEmail = normalizedEmail!; return Task.CompletedTask; }

    // ── IUserLockoutStore ─────────────────────────────────────────────────────

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(IdentityUser user, DateTimeOffset? lockoutEnd, CancellationToken ct)
    { user.LockoutEnd = lockoutEnd; return Task.CompletedTask; }

    public Task<int> IncrementAccessFailedCountAsync(IdentityUser user, CancellationToken ct)
    { user.AccessFailedCount++; return Task.FromResult(user.AccessFailedCount); }

    public Task ResetAccessFailedCountAsync(IdentityUser user, CancellationToken ct)
    { user.AccessFailedCount = 0; return Task.CompletedTask; }

    public Task<int> GetAccessFailedCountAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(IdentityUser user, bool enabled, CancellationToken ct)
    { user.LockoutEnabled = enabled; return Task.CompletedTask; }

    // ── IUserTwoFactorStore ───────────────────────────────────────────────────

    public Task<bool> GetTwoFactorEnabledAsync(IdentityUser user, CancellationToken ct)
        => Task.FromResult(user.TwoFactorEnabled);

    public Task SetTwoFactorEnabledAsync(IdentityUser user, bool enabled, CancellationToken ct)
    { user.TwoFactorEnabled = enabled; return Task.CompletedTask; }

    // ── IUserRoleStore ────────────────────────────────────────────────────────

    public async Task<IdentityResult> AddToRoleAsync(IdentityUser user, string normalizedRoleName, CancellationToken ct)
    {
        var roleId = await ScalarStringAsync(
            "SELECT id FROM ols_roles WHERE normalized_name = @n", ("@n", normalizedRoleName));
        if (roleId is null) return IdentityResult.Failed(IdentityErrors.InvalidRoleName(normalizedRoleName));

        await ExecAsync("""
            INSERT INTO ols_user_roles (user_id, role_id) VALUES (@u, @r)
            ON CONFLICT DO NOTHING
            """,
            ("@u", user.Id),
            ("@r", roleId));
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> RemoveFromRoleAsync(IdentityUser user, string normalizedRoleName, CancellationToken ct)
    {
        var rows = await ExecAsync("""
            DELETE FROM ols_user_roles
            WHERE user_id = @u
              AND role_id IN (SELECT id FROM ols_roles WHERE normalized_name = @n)
            """,
            ("@u", user.Id),
            ("@n", normalizedRoleName));
        return rows == 0
            ? IdentityResult.Failed(IdentityErrors.InvalidRoleName(normalizedRoleName))
            : IdentityResult.Success;
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(IdentityUser user, CancellationToken ct)
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.name FROM ols_roles r
            JOIN ols_user_roles ur ON ur.role_id = r.id
            WHERE ur.user_id = @u
            """;
        cmd.Parameters.AddWithValue("@u", user.Id);
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<bool> IsInRoleAsync(IdentityUser user, string normalizedRoleName, CancellationToken ct)
    {
        var n = Convert.ToInt64(await ScalarAsync("""
            SELECT COUNT(1) FROM ols_user_roles ur
            JOIN ols_roles r ON r.id = ur.role_id
            WHERE ur.user_id = @u AND r.normalized_name = @n
            """,
            ("@u", user.Id),
            ("@n", normalizedRoleName)));
        return n > 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IdentityUser?> FindOneAsync(string whereClause, string value, CancellationToken ct)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                   password_hash, security_stamp, concurrency_stamp,
                   phone_number, phone_number_confirmed, two_factor_enabled,
                   lockout_end, lockout_enabled, access_failed_count
            FROM   ols_users
            WHERE  {whereClause}
            """;
        cmd.Parameters.AddWithValue("@v", value);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new IdentityUser
        {
            Id                    = r.GetString(0),
            UserName              = r.IsDBNull(1) ? null! : r.GetString(1),
            NormalizedUserName    = r.IsDBNull(2) ? null! : r.GetString(2),
            Email                 = r.IsDBNull(3) ? null! : r.GetString(3),
            NormalizedEmail       = r.IsDBNull(4) ? null! : r.GetString(4),
            EmailConfirmed        = r.GetInt64(5) != 0,
            PasswordHash          = r.IsDBNull(6) ? null! : r.GetString(6),
            SecurityStamp         = r.IsDBNull(7) ? null! : r.GetString(7),
            ConcurrencyStamp      = r.IsDBNull(8) ? null! : r.GetString(8),
            PhoneNumber           = r.IsDBNull(9) ? null! : r.GetString(9),
            PhoneNumberConfirmed  = r.GetInt64(10) != 0,
            TwoFactorEnabled      = r.GetInt64(11) != 0,
            LockoutEnd            = r.IsDBNull(12) ? null : DateTimeOffset.Parse(r.GetString(12)),
            LockoutEnabled        = r.GetInt64(13) != 0,
            AccessFailedCount     = (int)r.GetInt64(14),
        };
    }

    private async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql, params (string Name, object? Value)[] p)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<string?> ScalarStringAsync(string sql, params (string Name, object? Value)[] p)
    {
        var o = await ScalarAsync(sql, p);
        return o is null or DBNull ? null : (string)o;
    }

    private static object N(string? s) => (object?)s ?? DBNull.Value;
}
