using Banyan.Identity;
using OLS.Root.Oidc.Models;
using OLS.Root.Oidc.Stores;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class SqliteOidcCodeAndTokenStoreTests : IAsyncLifetime
{
    private SqliteIdentityStore _id = null!;

    public async ValueTask InitializeAsync() => _id = await SqliteIdentityStore.OpenInMemoryAsync();
    public async ValueTask DisposeAsync()    => await _id.DisposeAsync();

    // ── AuthorizationCode ─────────────────────────────────────────────────────

    [Fact]
    public async Task AuthCode_StoreThenConsume_ReturnsAndDeletes()
    {
        var ac = new AuthorizationCode
        {
            Code                = "ac-1",
            ClientId            = "banyan-cli",
            SubjectId           = "user-1",
            RedirectUri         = "http://127.0.0.1/cb",
            CodeChallenge       = "challenge",
            CodeChallengeMethod = "S256",
            Nonce               = "nonce",
            State               = "state",
            RequestedScopes     = new[] { "openid", "profile" },
            CreatedAt           = DateTimeOffset.UtcNow,
            ExpiresAt           = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        await _id.AuthorizationCodes.StoreAsync(ac, default);

        var first = await _id.AuthorizationCodes.ConsumeAsync("ac-1", default);
        Assert.NotNull(first);
        Assert.Equal("user-1", first!.SubjectId);
        Assert.Equal(2, first.RequestedScopes.Count);

        // Single-use: second consume returns null
        Assert.Null(await _id.AuthorizationCodes.ConsumeAsync("ac-1", default));
    }

    [Fact]
    public async Task AuthCode_Consume_Unknown_ReturnsNull()
    {
        Assert.Null(await _id.AuthorizationCodes.ConsumeAsync("ghost", default));
    }

    // ── DeviceCode ────────────────────────────────────────────────────────────

    private static DeviceCode MakeDevice() => new()
    {
        Code            = "dev-code-1",
        UserCode        = "ABCD-1234",
        ClientId        = "banyan-cli",
        SubjectId       = null!,
        RequestedScopes = new[] { "openid", "banyan.full" },
        Interval        = 5,
        CreatedAt       = DateTimeOffset.UtcNow,
        ExpiresAt       = DateTimeOffset.UtcNow.AddMinutes(10),
    };

    [Fact]
    public async Task DeviceCode_FindByDeviceCodeAndUserCode()
    {
        await _id.DeviceCodes.StoreAsync(MakeDevice(), default);

        var byDev  = await _id.DeviceCodes.FindByDeviceCodeAsync("dev-code-1", default);
        var byUser = await _id.DeviceCodes.FindByUserCodeAsync("ABCD-1234", default);

        Assert.NotNull(byDev);
        Assert.NotNull(byUser);
        Assert.Equal(byDev!.Code, byUser!.Code);
        Assert.Equal(2, byDev.RequestedScopes.Count);
    }

    [Fact]
    public async Task DeviceCode_Update_PersistsAuthorisedAndPolledAt()
    {
        await _id.DeviceCodes.StoreAsync(MakeDevice(), default);

        var d = (await _id.DeviceCodes.FindByDeviceCodeAsync("dev-code-1", default))!;
        d.SubjectId    = "user-x";
        d.IsAuthorized = true;
        d.LastPolledAt = DateTimeOffset.UtcNow;
        await _id.DeviceCodes.UpdateAsync(d, default);

        var f = (await _id.DeviceCodes.FindByDeviceCodeAsync("dev-code-1", default))!;
        Assert.True(f.IsAuthorized);
        Assert.Equal("user-x", f.SubjectId);
        Assert.NotNull(f.LastPolledAt);
    }

    [Fact]
    public async Task DeviceCode_Remove_DeletesRow()
    {
        await _id.DeviceCodes.StoreAsync(MakeDevice(), default);
        await _id.DeviceCodes.RemoveAsync("dev-code-1", default);
        Assert.Null(await _id.DeviceCodes.FindByDeviceCodeAsync("dev-code-1", default));
    }

    // ── ReferenceToken ────────────────────────────────────────────────────────

    private static StoredToken MakeRef(string hash, string subjectId = "subj-1", string clientId = "banyan-cli") => new()
    {
        TokenHash = hash,
        SubjectId = subjectId,
        ClientId  = clientId,
        Scopes    = "openid profile",
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        IsRevoked = false,
    };

    [Fact]
    public async Task ReferenceToken_StoreFind_RoundTrips()
    {
        await _id.ReferenceTokens.StoreAsync(MakeRef("rt-h-1"), default);
        var f = await _id.ReferenceTokens.FindByHashAsync("rt-h-1", default);
        Assert.NotNull(f);
        Assert.Equal("subj-1", f!.SubjectId);
        Assert.False(f.IsRevoked);
    }

    [Fact]
    public async Task ReferenceToken_Revoke_SetsIsRevoked()
    {
        await _id.ReferenceTokens.StoreAsync(MakeRef("rt-h-2"), default);
        await _id.ReferenceTokens.RevokeAsync("rt-h-2", default);
        var f = await _id.ReferenceTokens.FindByHashAsync("rt-h-2", default);
        Assert.True(f!.IsRevoked);
    }

    [Fact]
    public async Task ReferenceToken_RevokeAll_OnlyMatchingSubjectAndClient()
    {
        await _id.ReferenceTokens.StoreAsync(MakeRef("rt-a", "subj-A", "cli-1"), default);
        await _id.ReferenceTokens.StoreAsync(MakeRef("rt-b", "subj-A", "cli-1"), default);
        await _id.ReferenceTokens.StoreAsync(MakeRef("rt-c", "subj-B", "cli-1"), default);
        await _id.ReferenceTokens.StoreAsync(MakeRef("rt-d", "subj-A", "cli-2"), default);

        await _id.ReferenceTokens.RevokeAllAsync("subj-A", "cli-1", default);

        Assert.True ((await _id.ReferenceTokens.FindByHashAsync("rt-a", default))!.IsRevoked);
        Assert.True ((await _id.ReferenceTokens.FindByHashAsync("rt-b", default))!.IsRevoked);
        Assert.False((await _id.ReferenceTokens.FindByHashAsync("rt-c", default))!.IsRevoked);
        Assert.False((await _id.ReferenceTokens.FindByHashAsync("rt-d", default))!.IsRevoked);
    }
}
