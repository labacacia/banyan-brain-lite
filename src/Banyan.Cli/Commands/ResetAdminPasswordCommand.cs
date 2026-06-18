// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity;
using Banyan.Identity.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OLS.Root.Core.Models;
using OLS.Root.Core.Security;

namespace Banyan.Cli.Commands;

internal static class ResetAdminPasswordCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts = CommandContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        var dbPath = CommandContext.ExpandHome(opts.DbPath);
        var keyPath = CommandContext.ExpandHome(opts.SigningKeyPath);
        var adminUser = CommandContext.GetOption(args, "--admin-username")
            ?? CommandContext.GetOption(args, "--username")
            ?? "admin";
        var adminPass = CommandContext.GetOption(args, "--admin-password")
            ?? CommandContext.GetOption(args, "--password");

        if (!File.Exists(keyPath))
        {
            Console.Error.WriteLine($"reset-admin-pwd: signing key not found at {keyPath}; run `banyan init` or start `banyan web` first");
            return 2;
        }
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"reset-admin-pwd: identity.db not found at {dbPath}; run `banyan init` or start `banyan web` first");
            return 2;
        }

        if (string.IsNullOrEmpty(adminPass))
            adminPass = CommandContext.PromptSecret($"New password for admin '{adminUser}': ");
        var confirm = CommandContext.GetOption(args, "--confirm-password");
        if (confirm is null && !CommandContext.HasFlag(args, "--no-confirm"))
            confirm = CommandContext.PromptSecret("Confirm new password: ");
        if (confirm is not null && adminPass != confirm)
        {
            Console.Error.WriteLine("reset-admin-pwd: passwords do not match");
            return 64;
        }
        if (adminPass.Length < 10)
        {
            Console.Error.WriteLine("reset-admin-pwd: password must be at least 10 characters");
            return 64;
        }

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
        var hasher = sp.GetRequiredService<IPasswordHasher<IdentityUser>>();
        var result = await AdminBootstrapper.ResetAdminPasswordAsync(store, hasher, adminUser, adminPass);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"reset-admin-pwd: {result.Message}");
            return 3;
        }

        Console.WriteLine($"Reset password for admin user '{adminUser}' in {dbPath}");
        return 0;
    }
}
