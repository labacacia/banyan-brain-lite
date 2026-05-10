using Banyan.Identity;
using OLS.Root.Core.Models;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class SqliteRoleStoreTests : IAsyncLifetime
{
    private SqliteIdentityStore _id = null!;

    public async ValueTask InitializeAsync() => _id = await SqliteIdentityStore.OpenInMemoryAsync();
    public async ValueTask DisposeAsync()    => await _id.DisposeAsync();

    [Fact]
    public async Task Create_AssignsIdAndConcurrencyStamp()
    {
        var role = new IdentityRole { Name = "admin", NormalizedName = "ADMIN" };
        var r = await _id.Roles.CreateAsync(role, default);
        Assert.True(r.Succeeded);
        Assert.False(string.IsNullOrEmpty(role.Id));
        Assert.False(string.IsNullOrEmpty(role.ConcurrencyStamp));
    }

    [Fact]
    public async Task Create_DuplicateNormalizedName_Fails()
    {
        await _id.Roles.CreateAsync(new IdentityRole { Name = "admin", NormalizedName = "ADMIN" }, default);
        var r = await _id.Roles.CreateAsync(new IdentityRole { Name = "Admin", NormalizedName = "ADMIN" }, default);
        Assert.False(r.Succeeded);
    }

    [Fact]
    public async Task FindByName_RoundTrips()
    {
        await _id.Roles.CreateAsync(new IdentityRole { Name = "operator", NormalizedName = "OPERATOR" }, default);
        var f = await _id.Roles.FindByNameAsync("OPERATOR", default);
        Assert.NotNull(f);
        Assert.Equal("operator", f!.Name);
    }

    [Fact]
    public async Task Update_RotatesConcurrencyStamp()
    {
        var role = new IdentityRole { Name = "viewer", NormalizedName = "VIEWER" };
        await _id.Roles.CreateAsync(role, default);
        var oldStamp = role.ConcurrencyStamp;
        role.Name = "viewers";
        role.NormalizedName = "VIEWERS";
        var r = await _id.Roles.UpdateAsync(role, default);
        Assert.True(r.Succeeded);
        Assert.NotEqual(oldStamp, role.ConcurrencyStamp);
    }

    [Fact]
    public async Task Delete_Removes()
    {
        var role = new IdentityRole { Name = "tmp", NormalizedName = "TMP" };
        await _id.Roles.CreateAsync(role, default);
        await _id.Roles.DeleteAsync(role, default);
        Assert.Null(await _id.Roles.FindByIdAsync(role.Id, default));
    }
}
