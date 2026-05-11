using Banyan.Identity.Crypto;
using OLS.Root.Core.Models;
using OLS.Root.Core.Results;
using OLS.Root.Core.Security;
using OLS.Root.Oidc.Models;

namespace Banyan.Identity;

public static class AdminBootstrapper
{
    public const string AdminRoleName = "admin";
    public const string AdminRoleNormalizedName = "ADMIN";

    public static void EnsureSigningKey(BanyanIdentityOptions opts)
    {
        var keyPath = ExpandHome(opts.SigningKeyPath);
        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (!File.Exists(keyPath))
            PemSigningKeyLoader.Generate(keyPath);
    }

    public static async Task EnsureBaselineAsync(SqliteIdentityStore store, BanyanIdentityOptions opts, CancellationToken ct = default)
    {
        await EnsureAdminRoleAsync(store, ct);
        await EnsureCliClientAsync(store, opts, ct);
    }

    public static Task<bool> HasAdminAsync(SqliteIdentityStore store, CancellationToken ct = default)
        => store.HasUsersInRoleAsync(AdminRoleNormalizedName, ct);

    public static async Task<IdentityResult> CreateInitialAdminAsync(
        SqliteIdentityStore store,
        IPasswordHasher<IdentityUser> hasher,
        string username,
        string password,
        CancellationToken ct = default)
    {
        username = username.Trim();
        var normalized = username.ToUpperInvariant();

        await EnsureAdminRoleAsync(store, ct);
        var existing = await store.Users.FindByNameAsync(normalized, ct);
        if (existing is not null)
            return IdentityResult.Failed(IdentityErrors.DuplicateUserName(username));

        var user = new IdentityUser
        {
            UserName = username,
            NormalizedUserName = normalized,
            Email = $"{username}@local",
            NormalizedEmail = $"{normalized}@LOCAL",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        user.PasswordHash = hasher.HashPassword(user, password);

        var created = await store.Users.CreateAsync(user, ct);
        if (!created.Succeeded) return created;

        return await store.Users.AddToRoleAsync(user, AdminRoleNormalizedName, ct);
    }

    public enum PasswordResetStatus
    {
        Success,
        UserNotFound,
        UserIsNotAdmin,
        UpdateFailed,
    }

    public sealed record PasswordResetResult(PasswordResetStatus Status, string Message)
    {
        public bool Succeeded => Status == PasswordResetStatus.Success;
    }

    public static async Task<PasswordResetResult> ResetAdminPasswordAsync(
        SqliteIdentityStore store,
        IPasswordHasher<IdentityUser> hasher,
        string username,
        string password,
        CancellationToken ct = default)
    {
        username = username.Trim();
        var user = await store.Users.FindByNameAsync(username.ToUpperInvariant(), ct);
        if (user is null)
            return new PasswordResetResult(PasswordResetStatus.UserNotFound, $"admin user '{username}' was not found");

        if (!await store.Users.IsInRoleAsync(user, AdminRoleNormalizedName, ct))
            return new PasswordResetResult(PasswordResetStatus.UserIsNotAdmin, $"user '{username}' is not an admin");

        user.PasswordHash = hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var update = await store.Users.UpdateAsync(user, ct);
        return update.Succeeded
            ? new PasswordResetResult(PasswordResetStatus.Success, $"password reset for '{username}'")
            : new PasswordResetResult(PasswordResetStatus.UpdateFailed, string.Join(", ", update.Errors.Select(e => e.Description)));
    }

    private static async Task EnsureAdminRoleAsync(SqliteIdentityStore store, CancellationToken ct)
    {
        if (await store.Roles.FindByNameAsync(AdminRoleNormalizedName, ct) is null)
        {
            await store.Roles.CreateAsync(new IdentityRole
            {
                Name = AdminRoleName,
                NormalizedName = AdminRoleNormalizedName,
            }, ct);
        }
    }

    private static Task EnsureCliClientAsync(SqliteIdentityStore store, BanyanIdentityOptions opts, CancellationToken ct)
    {
        var cliClient = new OidcClient
        {
            ClientId = opts.CliClientId,
            ClientName = "Banyan CLI",
            IsEnabled = true,
            RequireClientSecret = false,
            RequirePkce = true,
            SlidingRefreshTokenExpiry = true,
            AccessTokenLifetime = opts.AccessTokenExpiry,
            AuthorizationCodeLifetime = TimeSpan.FromMinutes(5),
            RefreshTokenLifetime = opts.RefreshTokenExpiry,
        };
        foreach (var redirectUri in opts.CliRedirectUris)
            cliClient.RedirectUris.Add(redirectUri);
        cliClient.AllowedScopes.Add("openid");
        cliClient.AllowedScopes.Add("profile");
        cliClient.AllowedScopes.Add("banyan.full");
        cliClient.AllowedGrantTypes.Add("authorization_code");
        cliClient.AllowedGrantTypes.Add("refresh_token");
        cliClient.AllowedGrantTypes.Add("urn:ietf:params:oauth:grant-type:device_code");

        return store.OidcClients.UpsertAsync(cliClient, ct);
    }

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
