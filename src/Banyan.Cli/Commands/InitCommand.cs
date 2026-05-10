using Banyan.Identity;
using Banyan.Identity.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OLS.Root.Core.Models;
using OLS.Root.Core.Security;

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
            AdminBootstrapper.EnsureSigningKey(opts);
        }
        var dbDir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);

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

        await AdminBootstrapper.EnsureBaselineAsync(store, opts);
        Console.WriteLine($"Upserted OIDC client '{opts.CliClientId}'");
        Console.WriteLine("Ensured role 'admin'");

        if (await AdminBootstrapper.HasAdminAsync(store))
        {
            Console.WriteLine("Admin user already exists — skipping creation");
            Console.WriteLine();
            Console.WriteLine("Banyan identity is initialised. Next: `banyan login`.");
            return 0;
        }

        // 3) Create initial admin user (interactive prompts if not given)
        if (string.IsNullOrEmpty(adminUser)) adminUser = CommandContext.Prompt("Admin username: ");
        if (string.IsNullOrEmpty(adminPass)) adminPass = CommandContext.PromptSecret("Admin password: ");
        if (adminPass!.Length < 10)
        {
            Console.Error.WriteLine("init: admin password must be at least 10 characters");
            return 64;
        }

        var hasher = sp.GetRequiredService<IPasswordHasher<IdentityUser>>();
        var create = await AdminBootstrapper.CreateInitialAdminAsync(store, hasher, adminUser!, adminPass!);
        if (!create.Succeeded)
        {
            Console.Error.WriteLine($"Failed to create admin user: {string.Join(", ", create.Errors.Select(e => e.Description))}");
            return 3;
        }

        Console.WriteLine($"Created admin user '{adminUser}'");
        Console.WriteLine();
        Console.WriteLine("Banyan identity initialised. Next: `banyan login`.");
        return 0;
    }
}
