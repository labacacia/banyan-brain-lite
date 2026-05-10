using Banyan.Identity.Crypto;
using Banyan.Identity.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OLS.Root.Authentication.Extensions;
using OLS.Root.Authentication.Stores;
using OLS.Root.Authorisation.Extensions;
using OLS.Root.Core.Extensions;
using OLS.Root.Core.Models;
using OLS.Root.Core.Stores;
using OLS.Root.Oidc.Extensions;
using OLS.Root.Oidc.Stores;

namespace Banyan.Identity.Extensions;

public static class BanyanIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Wires Banyan's SQLite-backed identity stores against OLS.Root and configures
    /// JWT/OIDC issuance from <see cref="BanyanIdentityOptions"/>. The signing key
    /// (PEM at <see cref="BanyanIdentityOptions.SigningKeyPath"/>) is loaded eagerly,
    /// so it must exist when this method is called.
    /// </summary>
    public static IServiceCollection AddBanyanIdentity(
        this IServiceCollection services,
        Action<BanyanIdentityOptions> configure)
    {
        // Capture options once for synchronous wiring of OLS DI.
        var opts = new BanyanIdentityOptions();
        configure(opts);
        services.Configure(configure);

        var keyPath = ExpandHome(opts.SigningKeyPath);
        var (signingKey, signingCreds) = PemSigningKeyLoader.Load(keyPath);

        // ── SqliteIdentityStore + 12 OLS store interface registrations ────────

        services.AddSingleton<SqliteIdentityStore>(sp =>
        {
            var resolved = sp.GetRequiredService<IOptions<BanyanIdentityOptions>>().Value;
            return SqliteIdentityStore.Open($"Data Source={ExpandHome(resolved.DbPath)}");
        });

        services.AddSingleton<IUserStore<IdentityUser>>           (sp => sp.GetRequiredService<SqliteIdentityStore>().Users);
        services.AddSingleton<IUserPasswordStore<IdentityUser>>   (sp => sp.GetRequiredService<SqliteIdentityStore>().Users);
        services.AddSingleton<IUserEmailStore<IdentityUser>>      (sp => sp.GetRequiredService<SqliteIdentityStore>().Users);
        services.AddSingleton<IUserLockoutStore<IdentityUser>>    (sp => sp.GetRequiredService<SqliteIdentityStore>().Users);
        services.AddSingleton<IUserRoleStore<IdentityUser>>       (sp => sp.GetRequiredService<SqliteIdentityStore>().Users);
        services.AddSingleton<IUserTwoFactorStore<IdentityUser>>  (sp => sp.GetRequiredService<SqliteIdentityStore>().Users);
        services.AddSingleton<IRoleStore<IdentityRole>>           (sp => sp.GetRequiredService<SqliteIdentityStore>().Roles);
        services.AddSingleton<IRefreshTokenStore<IdentityUser>>   (sp => sp.GetRequiredService<SqliteIdentityStore>().RefreshTokens);
        services.AddSingleton<IClientStore>                       (sp => sp.GetRequiredService<SqliteIdentityStore>().OidcClients);
        services.AddSingleton<IAuthorizationCodeStore>            (sp => sp.GetRequiredService<SqliteIdentityStore>().AuthorizationCodes);
        services.AddSingleton<IDeviceCodeStore>                   (sp => sp.GetRequiredService<SqliteIdentityStore>().DeviceCodes);
        services.AddSingleton<IReferenceTokenStore>               (sp => sp.GetRequiredService<SqliteIdentityStore>().ReferenceTokens);

        // ── OLS pipelines ─────────────────────────────────────────────────────

        services.AddOlsIdentityCore<IdentityUser>(_ => { });

        services.AddOlsAuthentication<IdentityUser>(authOpts =>
        {
            authOpts.Jwt.Issuer             = opts.Issuer;
            authOpts.Jwt.Audience           = opts.Audience;
            authOpts.Jwt.AccessTokenExpiry  = opts.AccessTokenExpiry;
            authOpts.Jwt.SigningKey         = signingKey;
            authOpts.RefreshToken.Expiry    = opts.RefreshTokenExpiry;
            authOpts.RefreshToken.RotationEnabled = true;
        });
        // OLS's AddRefreshTokenStore<TUser, TStore> uses ImplementationType registration (DI activates TStore via
        // reflection, requiring its ctor deps to be in DI). Our SqliteRefreshTokenStore takes a SqliteConnection
        // owned by SqliteIdentityStore, so we already registered IRefreshTokenStore<IdentityUser> by factory above.

        services.AddOlsAuthorisation(_ => { });

        services.AddOlsOidc(oidcOpts =>
        {
            oidcOpts.IssuerUri              = opts.Issuer;
            oidcOpts.SigningCredentials     = signingCreds;
            oidcOpts.DefaultAccessTokenLifetime = opts.AccessTokenExpiry;
        });

        return services;
    }

    /// <summary>Expand a leading <c>~</c> to the user profile directory.</summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
