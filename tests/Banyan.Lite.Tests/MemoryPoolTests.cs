// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Xunit;

namespace Banyan.Lite.Tests;

public sealed class MemoryPoolTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"banyan-pools-{Guid.NewGuid():N}.db");
    private SqliteMemoryStore _store = null!;
    private SqliteMemoryPoolRepository _repo = null!;

    public async ValueTask InitializeAsync()
    {
        _store = await SqliteMemoryStore.OpenAsync($"Data Source={_dbPath}", new HashingEmbedder());
        _repo = await SqliteMemoryPoolRepository.OpenAsync($"Data Source={_dbPath}");
    }

    public async ValueTask DisposeAsync()
    {
        await _repo.DisposeAsync();
        await _store.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PoolRepository_CreatesListsAndMaintainsMembers()
    {
        var pool = await _repo.CreateAsync("project", "workspace", "alice");

        Assert.Equal("local_workspace", pool.Scope);
        Assert.Equal(pool, await _repo.GetAsync(pool.Id));

        await _repo.AddMemberAsync(pool.Id, "agent-a", "agent");
        await _repo.RemoveMemberAsync(pool.Id, "agent-a");

        var pools = await _repo.ListAsync();
        Assert.Single(pools);
        Assert.Equal("project", pools[0].Name);
    }

    [Fact]
    public async Task PoolAwareStore_SearchesBoundPools()
    {
        var pool = await _repo.CreateAsync("workspace", "local_workspace", "alice");
        await _repo.BindAgentAsync("agent-a", pool.Id, priority: 10);

        await _store.WriteAsync(new WriteRequest("private banana memory", Namespace: "private"));
        await _store.WriteAsync(new WriteRequest("shared banyan memory", Namespace: LiteMemoryPool.Namespace(pool.Id)));

        var scoped = new PoolAwareMemoryStore(_store, _repo, "agent-a");
        var hits = await CollectAsync(scoped.SearchAsync(new SearchQuery("memory", Namespace: "private")));

        Assert.Contains(hits, h => h.Memory.Content == "private banana memory");
        Assert.Contains(hits, h => h.Memory.Content == "shared banyan memory");
    }

    [Fact]
    public async Task PoolAwareStore_DeduplicatesFanoutResultsByMemoryId()
    {
        var pool = await _repo.CreateAsync("session", "agent", "alice");
        await _repo.BindAgentAsync("agent-a", pool.Id, priority: 10);
        var poolNs = LiteMemoryPool.Namespace(pool.Id);

        await _store.WriteAsync(new WriteRequest("duplicate topic", Namespace: poolNs));

        var scoped = new PoolAwareMemoryStore(_store, _repo, "agent-a");
        var hits = await CollectAsync(scoped.SearchAsync(new SearchQuery("duplicate", Namespace: poolNs)));

        Assert.Single(hits);
        Assert.Equal(poolNs, hits[0].Memory.Namespace);
    }

    private static async Task<List<SearchHit>> CollectAsync(IAsyncEnumerable<SearchHit> hits)
    {
        var result = new List<SearchHit>();
        await foreach (var hit in hits) result.Add(hit);
        return result;
    }
}
