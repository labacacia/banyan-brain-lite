// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity;
using OLS.Root.Authentication.Models;
using OLS.Root.Core.Models;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class SqliteRefreshTokenStoreTests : IAsyncLifetime
{
    private SqliteIdentityStore _id = null!;
    private string _userId = "";

    public async ValueTask InitializeAsync()
    {
        _id = await SqliteIdentityStore.OpenInMemoryAsync();
        var u = new IdentityUser
        {
            UserName = "rtuser",
            NormalizedUserName = "RTUSER",
            Email = "rt@example.com",
            NormalizedEmail = "RT@EXAMPLE.COM",
            SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        await _id.Users.CreateAsync(u, default);
        _userId = u.Id;
    }

    public async ValueTask DisposeAsync() => await _id.DisposeAsync();

    private RefreshToken MakeToken(string hash) => new()
    {
        Id        = Guid.NewGuid().ToString("D"),
        UserId    = _userId,
        TokenHash = hash,
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        IsRevoked = false,
    };

    [Fact]
    public async Task Create_FindByHash_RoundTrips()
    {
        var t = MakeToken("hash-1");
        await _id.RefreshTokens.CreateAsync(t, default);
        var f = await _id.RefreshTokens.FindByHashAsync("hash-1", default);
        Assert.NotNull(f);
        Assert.Equal(t.Id, f!.Id);
        Assert.Equal(_userId, f.UserId);
    }

    [Fact]
    public async Task FindByHash_Unknown_ReturnsNull()
    {
        Assert.Null(await _id.RefreshTokens.FindByHashAsync("nope", default));
    }

    [Fact]
    public async Task Revoke_SetsIsRevokedAndReplacedBy()
    {
        var t = MakeToken("hash-2");
        await _id.RefreshTokens.CreateAsync(t, default);
        await _id.RefreshTokens.RevokeAsync(t.Id, "next-token-id", default);

        var f = await _id.RefreshTokens.FindByHashAsync("hash-2", default);
        Assert.NotNull(f);
        Assert.True(f!.IsRevoked);
        Assert.Equal("next-token-id", f.ReplacedByTokenId);
    }

    [Fact]
    public async Task RevokeAllForUser_RevokesActiveTokens()
    {
        await _id.RefreshTokens.CreateAsync(MakeToken("h-a"), default);
        await _id.RefreshTokens.CreateAsync(MakeToken("h-b"), default);
        await _id.RefreshTokens.CreateAsync(MakeToken("h-c"), default);

        await _id.RefreshTokens.RevokeAllForUserAsync(_userId, default);

        Assert.True((await _id.RefreshTokens.FindByHashAsync("h-a", default))!.IsRevoked);
        Assert.True((await _id.RefreshTokens.FindByHashAsync("h-b", default))!.IsRevoked);
        Assert.True((await _id.RefreshTokens.FindByHashAsync("h-c", default))!.IsRevoked);
    }
}
