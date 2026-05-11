using Banyan.Auth.Stores;
using NPS.NIP.Ca;
using Xunit;

namespace Banyan.Auth.Tests;

public sealed class SqliteNipCaStoreTests : IAsyncLifetime
{
    private SqliteNipCaStore _store = null!;

    public async ValueTask InitializeAsync() => _store = await SqliteNipCaStore.OpenInMemoryAsync();
    public async ValueTask DisposeAsync()    => await _store.DisposeAsync();

    private static NipCertRecord MakeRecord(
        string id, string serial,
        string entityType = "agent",
        string[]? caps = null,
        string pubKey = "ed25519:abc123",
        DateTime? issuedAt = null) =>
        new()
        {
            Nid           = $"urn:nps:{entityType}:local.banyan:{id}",
            Serial        = serial,
            EntityType    = entityType,
            PubKey        = pubKey,
            Capabilities  = caps ?? Array.Empty<string>(),
            ScopeJson     = "{}",
            MetadataJson  = "{}",
            IssuedBy      = "urn:nps:ca:local.banyan:root",
            IssuedAt      = issuedAt ?? DateTime.UtcNow,
            ExpiresAt     = (issuedAt ?? DateTime.UtcNow).AddDays(30),
        };

    [Fact]
    public async Task NextSerial_IsMonotonic_StartsAtOne()
    {
        var first  = await _store.NextSerialAsync(default);
        var second = await _store.NextSerialAsync(default);
        var third  = await _store.NextSerialAsync(default);
        Assert.Equal("0000000000000001", first);
        Assert.Equal("0000000000000002", second);
        Assert.Equal("0000000000000003", third);
    }

    [Fact]
    public async Task NextSerial_IsZeroPadded16Hex()
    {
        var s = await _store.NextSerialAsync(default);
        Assert.Equal(16, s.Length);
        Assert.Matches(@"^[0-9a-f]+$", s);
    }

    [Fact]
    public async Task Save_RoundTripsAllFields()
    {
        var rec = MakeRecord("alice", "0000000000000001", "agent", new[] { "memory.read", "memory.write" });
        await _store.SaveAsync(rec, default);

        var got = await _store.GetByNidAsync(rec.Nid, default);
        Assert.NotNull(got);
        Assert.Equal(rec.Nid,        got!.Nid);
        Assert.Equal(rec.Serial,     got.Serial);
        Assert.Equal(rec.EntityType, got.EntityType);
        Assert.Equal(rec.PubKey,     got.PubKey);
        Assert.Equal(2,              got.Capabilities.Length);
        Assert.Contains("memory.read",  got.Capabilities);
        Assert.Contains("memory.write", got.Capabilities);
        Assert.Null(got.RevokedAt);
    }

    [Fact]
    public async Task GetBySerial_FindsByUniqueSerial()
    {
        await _store.SaveAsync(MakeRecord("bob",   "00000000000000aa"), default);
        await _store.SaveAsync(MakeRecord("carol", "00000000000000bb"), default);

        var f = await _store.GetBySerialAsync("00000000000000bb", default);
        Assert.NotNull(f);
        Assert.Contains("carol", f!.Nid);
    }

    [Fact]
    public async Task Save_UpsertOnDuplicateNid()
    {
        var first  = MakeRecord("dave", "0000000000000010", pubKey: "ed25519:original");
        var second = MakeRecord("dave", "0000000000000010", pubKey: "ed25519:rotated");
        await _store.SaveAsync(first,  default);
        await _store.SaveAsync(second, default);

        var got = await _store.GetByNidAsync(first.Nid, default);
        Assert.Equal("ed25519:rotated", got!.PubKey);
    }

    [Fact]
    public async Task Revoke_MarksRecord_AndReturnsTrue()
    {
        var rec = MakeRecord("erin", "0000000000000020");
        await _store.SaveAsync(rec, default);

        var ok = await _store.RevokeAsync(rec.Nid, "compromised", DateTime.UtcNow, default);
        Assert.True(ok);

        var got = await _store.GetByNidAsync(rec.Nid, default);
        Assert.NotNull(got!.RevokedAt);
        Assert.Equal("compromised", got.RevokeReason);
    }

    [Fact]
    public async Task Revoke_AlreadyRevoked_ReturnsFalse()
    {
        var rec = MakeRecord("frank", "0000000000000030");
        await _store.SaveAsync(rec, default);

        Assert.True (await _store.RevokeAsync(rec.Nid, "first",  DateTime.UtcNow, default));
        Assert.False(await _store.RevokeAsync(rec.Nid, "second", DateTime.UtcNow, default));
    }

    [Fact]
    public async Task Revoke_UnknownNid_ReturnsFalse()
    {
        var ok = await _store.RevokeAsync("urn:nps:agent:local.banyan:ghost", "x", DateTime.UtcNow, default);
        Assert.False(ok);
    }

    [Fact]
    public async Task GetRevoked_ReturnsOnlyRevokedRecords()
    {
        await _store.SaveAsync(MakeRecord("a", "0000000000000040"), default);
        await _store.SaveAsync(MakeRecord("b", "0000000000000041"), default);
        await _store.SaveAsync(MakeRecord("c", "0000000000000042"), default);
        await _store.RevokeAsync("urn:nps:agent:local.banyan:b", "x", DateTime.UtcNow, default);

        var revoked = await _store.GetRevokedAsync(default);
        Assert.Single(revoked);
        Assert.Contains("b", revoked[0].Nid);
    }

    [Fact]
    public async Task List_ReturnsAll_OrderedByIssuedAtDesc()
    {
        await _store.SaveAsync(MakeRecord("old", "0000000000000050", issuedAt: DateTime.UtcNow.AddDays(-10)), default);
        await _store.SaveAsync(MakeRecord("new", "0000000000000051", issuedAt: DateTime.UtcNow),              default);

        var all = await _store.ListAsync(revokedOnly: false, default);
        Assert.Equal(2, all.Count);
        Assert.Contains("new", all[0].Nid);
    }
}
