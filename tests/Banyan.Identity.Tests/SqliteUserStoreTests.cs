// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity;
using OLS.Root.Core.Models;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class SqliteUserStoreTests : IAsyncLifetime
{
    private SqliteIdentityStore _id = null!;

    public async ValueTask InitializeAsync() => _id = await SqliteIdentityStore.OpenInMemoryAsync();
    public async ValueTask DisposeAsync()    => await _id.DisposeAsync();

    private static IdentityUser MakeUser(string username, string? email = null) => new()
    {
        UserName           = username,
        NormalizedUserName = username.ToUpperInvariant(),
        Email              = email ?? $"{username}@example.com",
        NormalizedEmail    = (email ?? $"{username}@example.com").ToUpperInvariant(),
        EmailConfirmed     = true,
        SecurityStamp      = Guid.NewGuid().ToString("N"),
    };

    [Fact]
    public async Task Create_AssignsIdAndConcurrencyStamp()
    {
        var u = MakeUser("alice");
        var r = await _id.Users.CreateAsync(u, default);
        Assert.True(r.Succeeded);
        Assert.False(string.IsNullOrEmpty(u.Id));
        Assert.False(string.IsNullOrEmpty(u.ConcurrencyStamp));
    }

    [Fact]
    public async Task Create_DuplicateUserName_FailsWithConstraint()
    {
        await _id.Users.CreateAsync(MakeUser("bob"), default);
        var r = await _id.Users.CreateAsync(MakeUser("bob"), default);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public async Task FindById_RoundTripsAllFields()
    {
        var u = MakeUser("carol");
        u.PhoneNumber          = "+61400000000";
        u.PhoneNumberConfirmed = true;
        u.LockoutEnd           = DateTimeOffset.UtcNow.AddDays(1);
        await _id.Users.CreateAsync(u, default);

        var f = await _id.Users.FindByIdAsync(u.Id, default);
        Assert.NotNull(f);
        Assert.Equal("carol",                f!.UserName);
        Assert.Equal("CAROL",                f.NormalizedUserName);
        Assert.Equal("carol@example.com",    f.Email);
        Assert.True(f.EmailConfirmed);
        Assert.Equal("+61400000000",         f.PhoneNumber);
        Assert.True(f.PhoneNumberConfirmed);
        Assert.NotNull(f.LockoutEnd);
    }

    [Fact]
    public async Task FindByName_FindsByNormalizedUserName()
    {
        await _id.Users.CreateAsync(MakeUser("dave"), default);
        var f = await _id.Users.FindByNameAsync("DAVE", default);
        Assert.NotNull(f);
        Assert.Equal("dave", f!.UserName);
    }

    [Fact]
    public async Task FindByEmail_FindsByNormalizedEmail()
    {
        await _id.Users.CreateAsync(MakeUser("erin", "Erin@Example.com"), default);
        var f = await _id.Users.FindByEmailAsync("ERIN@EXAMPLE.COM", default);
        Assert.NotNull(f);
        Assert.Equal("erin", f!.UserName);
    }

    [Fact]
    public async Task Update_RotatesConcurrencyStamp_AndPersistsChanges()
    {
        var u = MakeUser("frank");
        await _id.Users.CreateAsync(u, default);
        var oldStamp = u.ConcurrencyStamp;

        u.Email = "new@example.com";
        u.NormalizedEmail = "NEW@EXAMPLE.COM";
        var r = await _id.Users.UpdateAsync(u, default);

        Assert.True(r.Succeeded);
        Assert.NotEqual(oldStamp, u.ConcurrencyStamp);

        var f = await _id.Users.FindByIdAsync(u.Id, default);
        Assert.Equal("new@example.com", f!.Email);
    }

    [Fact]
    public async Task Update_StaleConcurrencyStamp_Fails()
    {
        var u = MakeUser("gina");
        await _id.Users.CreateAsync(u, default);
        u.ConcurrencyStamp = "tampered";
        var r = await _id.Users.UpdateAsync(u, default);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public async Task Delete_RemovesUser()
    {
        var u = MakeUser("hank");
        await _id.Users.CreateAsync(u, default);
        var r = await _id.Users.DeleteAsync(u, default);
        Assert.True(r.Succeeded);
        Assert.Null(await _id.Users.FindByIdAsync(u.Id, default));
    }

    [Fact]
    public async Task Password_GetSetHasOnlyMutateInMemory()
    {
        var u = MakeUser("ivy");
        Assert.False(await _id.Users.HasPasswordAsync(u, default));
        await _id.Users.SetPasswordHashAsync(u, "h@sh", default);
        Assert.True(await _id.Users.HasPasswordAsync(u, default));
        Assert.Equal("h@sh", await _id.Users.GetPasswordHashAsync(u, default));
    }

    [Fact]
    public async Task Lockout_IncrementAndResetAccessFailedCount()
    {
        var u = MakeUser("jane");
        Assert.Equal(0, await _id.Users.GetAccessFailedCountAsync(u, default));
        await _id.Users.IncrementAccessFailedCountAsync(u, default);
        await _id.Users.IncrementAccessFailedCountAsync(u, default);
        Assert.Equal(2, await _id.Users.GetAccessFailedCountAsync(u, default));
        await _id.Users.ResetAccessFailedCountAsync(u, default);
        Assert.Equal(0, await _id.Users.GetAccessFailedCountAsync(u, default));
    }

    [Fact]
    public async Task Roles_AddRemoveListChecksMembership()
    {
        var role = new IdentityRole { Name = "admin", NormalizedName = "ADMIN" };
        await _id.Roles.CreateAsync(role, default);

        var u = MakeUser("kate");
        await _id.Users.CreateAsync(u, default);

        Assert.False(await _id.Users.IsInRoleAsync(u, "ADMIN", default));
        var add = await _id.Users.AddToRoleAsync(u, "ADMIN", default);
        Assert.True(add.Succeeded);
        Assert.True(await _id.Users.IsInRoleAsync(u, "ADMIN", default));

        var roles = await _id.Users.GetRolesAsync(u, default);
        Assert.Single(roles);
        Assert.Equal("admin", roles[0]);

        var rm = await _id.Users.RemoveFromRoleAsync(u, "ADMIN", default);
        Assert.True(rm.Succeeded);
        Assert.False(await _id.Users.IsInRoleAsync(u, "ADMIN", default));
    }

    [Fact]
    public async Task AddToRole_UnknownRole_Fails()
    {
        var u = MakeUser("liam");
        await _id.Users.CreateAsync(u, default);
        var r = await _id.Users.AddToRoleAsync(u, "DOES_NOT_EXIST", default);
        Assert.False(r.Succeeded);
    }
}
