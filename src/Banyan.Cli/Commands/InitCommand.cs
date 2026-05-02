using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OLS.Root.Core.Models;
using OLS.Root.Core.Security;
using OLS.Root.Oidc.Models;

namespace Banyan.Cli.Commands;

internal static class InitCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts        = CommandContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        var keyPath     = CommandContext.ExpandHome(opts.SigningKeyPath);
        var dbPath      = CommandContext.ExpandHome(opts.DbPath);
        var adminUser   = CommandContext.GetOption(args, "--admin-username");
        var adminPass   = CommandContext.GetOption(args, "--admin-password");

        // 1) Ensure signing key
        if (!File.Exists(keyPath))
        {
            Console.WriteLine($"No signing key at {keyPath} — generating 2048-bit RSA");
            PemSigningKeyLoader.Generate(keyPath);
        }

        // 2) Spin up DI to get IPasswordHasher + open identity.db
        var services = new ServiceCollection();
        services.AddBanyanIdentity(o =>
        {
            o.DbPath              = opts.DbPath;
            o.SigningKeyPath      = opts.SigningKeyPath;
            o.Issuer              = opts.Issuer;
            o.Audience            = opts.Audience;
            o.AccessTokenExpiry   = opts.AccessTokenExpiry;
            o.RefreshTokenExpiry  = opts.RefreshTokenExpiry;
            o.CliClientId         = opts.CliClientId;
            o.CliRedirectUris     = opts.CliRedirectUris;
        });
        await using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<SqliteIdentityStore>();
        Console.WriteLine($"Opened identity.db at {dbPath}");

        // 3) Upsert the CLI OIDC client (public, PKCE)
        var cliClient = new OidcClient
        {
            ClientId                  = opts.CliClientId,
            ClientName                = "Banyan CLI",
            IsEnabled                 = true,
            RequireClientSecret       = false,
            RequirePkce               = true,
            SlidingRefreshTokenExpiry = true,
            AccessTokenLifetime       = opts.AccessTokenExpiry,
            AuthorizationCodeLifetime = TimeSpan.FromMinutes(5),
            RefreshTokenLifetime      = opts.RefreshTokenExpiry,
        };
        foreach (var ru in opts.CliRedirectUris) cliClient.RedirectUris.Add(ru);
        cliClient.AllowedScopes.Add("openid");
        cliClient.AllowedScopes.Add("profile");
        cliClient.AllowedScopes.Add("banyan.full");
        cliClient.AllowedGrantTypes.Add("authorization_code");
        cliClient.AllowedGrantTypes.Add("refresh_token");
        cliClient.AllowedGrantTypes.Add("urn:ietf:params:oauth:grant-type:device_code");

        await store.OidcClients.UpsertAsync(cliClient);
        Console.WriteLine($"Upserted OIDC client '{opts.CliClientId}'");

        // 4) Ensure 'admin' role exists
        var adminRole = await store.Roles.FindByNameAsync("ADMIN", default);
        if (adminRole is null)
        {
            adminRole = new IdentityRole { Name = "admin", NormalizedName = "ADMIN" };
            await store.Roles.CreateAsync(adminRole, default);
            Console.WriteLine("Created role 'admin'");
        }

        // 5) Create initial admin user (interactive prompts if not given)
        if (string.IsNullOrEmpty(adminUser)) adminUser = Prompt("Admin username: ");
        if (string.IsNullOrEmpty(adminPass)) adminPass = PromptSecret("Admin password: ");

        var existing = await store.Users.FindByNameAsync(adminUser!.ToUpperInvariant(), default);
        if (existing is not null)
        {
            Console.WriteLine($"Admin user '{adminUser}' already exists — skipping creation");
            return 0;
        }

        var hasher = sp.GetRequiredService<IPasswordHasher<IdentityUser>>();
        var user = new IdentityUser
        {
            UserName             = adminUser,
            NormalizedUserName   = adminUser.ToUpperInvariant(),
            Email                = $"{adminUser}@local",
            NormalizedEmail      = $"{adminUser.ToUpperInvariant()}@LOCAL",
            EmailConfirmed       = true,
            SecurityStamp        = Guid.NewGuid().ToString("N"),
        };
        user.PasswordHash = hasher.HashPassword(user, adminPass!);
        var create = await store.Users.CreateAsync(user, default);
        if (!create.Succeeded)
        {
            Console.Error.WriteLine($"Failed to create admin user: {string.Join(", ", create.Errors.Select(e => e.Description))}");
            return 3;
        }
        await store.Users.AddToRoleAsync(user, "ADMIN", default);

        Console.WriteLine($"Created admin user '{adminUser}' (id={user.Id})");
        Console.WriteLine();
        Console.WriteLine("Banyan identity initialised. Next: `banyan login`.");
        return 0;
    }

    private static string Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine() ?? "";
    }

    private static string PromptSecret(string label)
    {
        Console.Write(label);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (k.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        return sb.ToString();
    }
}
