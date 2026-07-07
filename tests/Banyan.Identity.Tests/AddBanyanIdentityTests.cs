// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Microsoft.Extensions.DependencyInjection;
using InnoLotus.Root.Authentication.Stores;
using InnoLotus.Root.Core.Models;
using InnoLotus.Root.Core.Stores;
using InnoLotus.Root.Oidc.Stores;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class AddBanyanIdentityTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-di-" + Guid.NewGuid().ToString("N")[..8]);

    public AddBanyanIdentityTests()
    {
        Directory.CreateDirectory(_tmpDir);
        PemSigningKeyLoader.Generate(Path.Combine(_tmpDir, "signing.pem"));
    }
    public void Dispose() { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddBanyanIdentity(o =>
        {
            o.DbPath         = Path.Combine(_tmpDir, "id.db");
            o.SigningKeyPath = Path.Combine(_tmpDir, "signing.pem");
            o.Issuer         = "https://example.test";
            o.Audience       = "banyan-test";
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolves_All_OLS_Store_Interfaces_To_SqliteImpls()
    {
        using var sp = Build();

        Assert.IsType<Stores.SqliteUserStore>            (sp.GetRequiredService<IUserStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteUserStore>            (sp.GetRequiredService<IUserPasswordStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteUserStore>            (sp.GetRequiredService<IUserEmailStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteUserStore>            (sp.GetRequiredService<IUserLockoutStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteUserStore>            (sp.GetRequiredService<IUserRoleStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteUserStore>            (sp.GetRequiredService<IUserTwoFactorStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteRoleStore>            (sp.GetRequiredService<IRoleStore<IdentityRole>>());
        Assert.IsType<Stores.SqliteRefreshTokenStore>    (sp.GetRequiredService<IRefreshTokenStore<IdentityUser>>());
        Assert.IsType<Stores.SqliteOidcClientStore>      (sp.GetRequiredService<IClientStore>());
        Assert.IsType<Stores.SqliteAuthorizationCodeStore>(sp.GetRequiredService<IAuthorizationCodeStore>());
        Assert.IsType<Stores.SqliteDeviceCodeStore>      (sp.GetRequiredService<IDeviceCodeStore>());
        Assert.IsType<Stores.SqliteReferenceTokenStore>  (sp.GetRequiredService<IReferenceTokenStore>());
    }

    [Fact]
    public void All_UserStore_Interfaces_ShareSameInstance()
    {
        // Six user-store interfaces are all backed by one SqliteUserStore — verify
        // DI factories return the same instance, otherwise UserManager would corrupt state.
        using var sp = Build();
        var u1 = sp.GetRequiredService<IUserStore<IdentityUser>>();
        var u2 = sp.GetRequiredService<IUserPasswordStore<IdentityUser>>();
        var u3 = sp.GetRequiredService<IUserEmailStore<IdentityUser>>();
        Assert.Same(u1, u2);
        Assert.Same(u1, u3);
    }

    [Fact]
    public void Db_Is_Migrated_OnFirstResolve()
    {
        using var sp = Build();
        // Resolving any store triggers SqliteIdentityStore creation, which runs migrations.
        _ = sp.GetRequiredService<IUserStore<IdentityUser>>();
        var dbPath = Path.Combine(_tmpDir, "id.db");
        Assert.True(File.Exists(dbPath));
    }
}
