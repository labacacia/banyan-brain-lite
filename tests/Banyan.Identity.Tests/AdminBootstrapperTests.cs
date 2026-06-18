// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Microsoft.Extensions.DependencyInjection;
using OLS.Root.Core.Models;
using OLS.Root.Core.Security;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class AdminBootstrapperTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-admin-bootstrap-" + Guid.NewGuid().ToString("N")[..8]);

    public AdminBootstrapperTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public async Task CreateInitialAdmin_ThenResetPassword_UpdatesAdminPasswordHash()
    {
        var opts = new BanyanIdentityOptions
        {
            DbPath = Path.Combine(_tmpDir, "identity.db"),
            SigningKeyPath = Path.Combine(_tmpDir, "signing.pem"),
            Issuer = "http://placeholder",
            Audience = "banyan-test",
        };
        PemSigningKeyLoader.Generate(opts.SigningKeyPath);

        var services = new ServiceCollection();
        services.AddBanyanIdentity(o =>
        {
            o.DbPath = opts.DbPath;
            o.SigningKeyPath = opts.SigningKeyPath;
            o.Issuer = opts.Issuer;
            o.Audience = opts.Audience;
        });
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<SqliteIdentityStore>();
        var hasher = sp.GetRequiredService<IPasswordHasher<IdentityUser>>();

        await AdminBootstrapper.EnsureBaselineAsync(store, opts);
        var create = await AdminBootstrapper.CreateInitialAdminAsync(store, hasher, "admin", "Old$ecret-2026");
        Assert.True(create.Succeeded);

        var before = await store.Users.FindByNameAsync("ADMIN", default);
        Assert.NotNull(before);
        var oldHash = before!.PasswordHash;

        var reset = await AdminBootstrapper.ResetAdminPasswordAsync(store, hasher, "admin", "New$ecret-2026");
        Assert.True(reset.Succeeded);

        var after = await store.Users.FindByNameAsync("ADMIN", default);
        Assert.NotNull(after);
        Assert.NotEqual(oldHash, after!.PasswordHash);
    }

    [Fact]
    public async Task ResetPassword_ForNonAdminUser_Fails()
    {
        var opts = new BanyanIdentityOptions
        {
            DbPath = Path.Combine(_tmpDir, "nonadmin-identity.db"),
            SigningKeyPath = Path.Combine(_tmpDir, "nonadmin-signing.pem"),
            Issuer = "http://placeholder",
            Audience = "banyan-test",
        };
        PemSigningKeyLoader.Generate(opts.SigningKeyPath);
        var services = new ServiceCollection();
        services.AddBanyanIdentity(o =>
        {
            o.DbPath = opts.DbPath;
            o.SigningKeyPath = opts.SigningKeyPath;
            o.Issuer = opts.Issuer;
            o.Audience = opts.Audience;
        });
        await using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<SqliteIdentityStore>();
        var hasher = sp.GetRequiredService<IPasswordHasher<IdentityUser>>();

        await AdminBootstrapper.EnsureBaselineAsync(store, opts);
        var user = new IdentityUser
        {
            UserName = "bob",
            NormalizedUserName = "BOB",
            Email = "bob@local",
            NormalizedEmail = "BOB@LOCAL",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        user.PasswordHash = hasher.HashPassword(user, "B0bs$ecret-2026");
        await store.Users.CreateAsync(user, default);

        var reset = await AdminBootstrapper.ResetAdminPasswordAsync(store, hasher, "bob", "New$ecret-2026");
        Assert.Equal(AdminBootstrapper.PasswordResetStatus.UserIsNotAdmin, reset.Status);
    }
}
