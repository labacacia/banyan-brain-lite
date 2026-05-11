using Banyan.Identity;
using OLS.Root.Oidc.Models;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class SqliteOidcClientStoreTests : IAsyncLifetime
{
    private SqliteIdentityStore _id = null!;

    public async ValueTask InitializeAsync() => _id = await SqliteIdentityStore.OpenInMemoryAsync();
    public async ValueTask DisposeAsync()    => await _id.DisposeAsync();

    private static OidcClient MakeCli() => new()
    {
        ClientId                  = "banyan-cli",
        ClientName                = "Banyan CLI",
        IsEnabled                 = true,
        RequireClientSecret       = false,
        RequirePkce               = true,
        SlidingRefreshTokenExpiry = true,
        AccessTokenLifetime       = TimeSpan.FromMinutes(30),
        AuthorizationCodeLifetime = TimeSpan.FromMinutes(5),
        RefreshTokenLifetime      = TimeSpan.FromDays(30),
        RedirectUris              = { "http://127.0.0.1", "http://localhost" },
        AllowedScopes             = { "openid", "profile", "banyan.full" },
        AllowedGrantTypes         = { "authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code" },
        AllowedCorsOrigins        = { "https://localhost:5001" },
        PostLogoutRedirectUris    = { "http://127.0.0.1/loggedout" },
    };

    [Fact]
    public async Task Find_Unknown_ReturnsNull()
    {
        Assert.Null(await _id.OidcClients.FindByClientIdAsync("nope", default));
    }

    [Fact]
    public async Task Upsert_Then_Find_RoundTripsScalarsAndLists()
    {
        var c = MakeCli();
        await _id.OidcClients.UpsertAsync(c);

        var f = await _id.OidcClients.FindByClientIdAsync("banyan-cli", default);
        Assert.NotNull(f);
        Assert.Equal("Banyan CLI", f!.ClientName);
        Assert.True(f.RequirePkce);
        Assert.False(f.RequireClientSecret);
        Assert.Equal(TimeSpan.FromMinutes(30), f.AccessTokenLifetime);
        Assert.Equal(2, f.RedirectUris.Count);
        Assert.Contains("http://127.0.0.1",        f.RedirectUris);
        Assert.Contains("openid",                  f.AllowedScopes);
        Assert.Contains("authorization_code",      f.AllowedGrantTypes);
        Assert.Contains("https://localhost:5001",  f.AllowedCorsOrigins);
        Assert.Contains("http://127.0.0.1/loggedout", f.PostLogoutRedirectUris);
    }

    [Fact]
    public async Task Upsert_Twice_ReplacesChildRows()
    {
        var c = MakeCli();
        await _id.OidcClients.UpsertAsync(c);

        c.RedirectUris.Clear();
        c.RedirectUris.Add("https://newredirect/cb");
        await _id.OidcClients.UpsertAsync(c);

        var f = await _id.OidcClients.FindByClientIdAsync("banyan-cli", default);
        Assert.NotNull(f);
        Assert.Single(f!.RedirectUris);
        Assert.Equal("https://newredirect/cb", f.RedirectUris[0]);
    }

    [Fact]
    public async Task Upsert_PreservesHashedSecrets()
    {
        var c = MakeCli();
        c.RequireClientSecret = true;
        c.HashedSecrets.Add("h-secret-1");
        c.HashedSecrets.Add("h-secret-2");
        await _id.OidcClients.UpsertAsync(c);

        var f = await _id.OidcClients.FindByClientIdAsync("banyan-cli", default);
        Assert.Equal(2, f!.HashedSecrets.Count);
        Assert.Contains("h-secret-1", f.HashedSecrets);
    }
}
