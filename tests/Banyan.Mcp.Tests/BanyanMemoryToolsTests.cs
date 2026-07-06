// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Banyan.Core;
using Banyan.Mcp;
using Xunit;

namespace Banyan.Mcp.Tests;

public sealed class BanyanMemoryToolsTests
{
    [Fact]
    public async Task ListAsync_NoNamespace_DelegatesToListQuery()
    {
        var store = new FakeStore();
        var tools = Tools(store);

        await tools.ListAsync(limit: 3);

        Assert.NotNull(store.LastList);
        Assert.Equal(3, store.LastList!.Limit);
        Assert.Null(store.LastList.Namespace);
    }

    [Fact]
    public async Task ListAsync_ExplicitNamespace_NarrowsListQuery()
    {
        var store = new FakeStore();
        var tools = Tools(store);

        await tools.ListAsync("alpha", 2);

        Assert.NotNull(store.LastList);
        Assert.Equal(2, store.LastList!.Limit);
        Assert.Equal("alpha", store.LastList.Namespace);
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestFirstRowsFromStore()
    {
        var first = new Memory(
            MemoryId.New(),
            EventId.New(),
            "alpha",
            "newer memory",
            null,
            null,
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-03T00:00:00Z"));
        var second = new Memory(
            MemoryId.New(),
            EventId.New(),
            "beta",
            "older memory",
            null,
            null,
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-07-02T00:00:00Z"));
        var tools = Tools(new FakeStore(first, second));

        var text = await tools.ListAsync(limit: 2);

        Assert.Contains($"[1] id={first.Id}  ns=alpha  updated=2026-07-03T00:00:00.0000000+00:00", text);
        Assert.Contains("newer memory", text);
        Assert.Contains($"[2] id={second.Id}  ns=beta  updated=2026-07-02T00:00:00.0000000+00:00", text);
        Assert.Contains("older memory", text);
    }

    [Fact]
    public async Task ListAsync_EmptyStore_ReturnsNoMemoriesMessage()
    {
        var tools = Tools(new FakeStore());

        var text = await tools.ListAsync();

        Assert.Equal("No memories found.", text);
    }

    [Fact]
    public async Task RememberAsync_UsesContextDefaultWriteNamespaceBeforeStaticDefault()
    {
        var store = new FakeStore();
        var tools = Tools(store, new TestAgentContext("urn:nps:agent:acme:alice", "acme"));

        await tools.RememberAsync("remember this");

        Assert.NotNull(store.LastWrite);
        Assert.Equal("acme", store.LastWrite!.Namespace);
        Assert.Equal("urn:nps:agent:acme:alice", store.LastWrite.AgentNid);
    }

    [Fact]
    public async Task RememberAsync_UsesStaticDefaultWhenContextHasNoNamespace()
    {
        var store = new FakeStore();
        var tools = Tools(store);

        await tools.RememberAsync("remember this");

        Assert.NotNull(store.LastWrite);
        Assert.Equal("default", store.LastWrite!.Namespace);
    }

    private static BanyanMemoryTools Tools(
        IMemoryStore store,
        IBanyanMcpAgentContext? context = null)
        => new(
            new StaticBanyanMcpMemoryStoreAccessor(store),
            new McpDefaults("default"),
            context ?? new NullBanyanMcpAgentContext());

    private sealed record TestAgentContext(
        string? CurrentAgentNid,
        string? DefaultWriteNamespace) : IBanyanMcpAgentContext;

    private sealed class FakeStore(params Memory[] memories) : IMemoryStore
    {
        public MemoryListQuery? LastList { get; private set; }
        public WriteRequest? LastWrite { get; private set; }

        public Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
        {
            LastWrite = req;
            return Task.FromResult(MemoryId.New());
        }

        public Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
            => Task.FromResult(EventId.New());

        public Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(EventId.New());

        public Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
            => Task.FromResult<Memory?>(null);

        public Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Memory>>([]);

        public async IAsyncEnumerable<SearchHit> SearchAsync(
            SearchQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<Memory> ListAsync(
            MemoryListQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastList = query;
            await Task.CompletedTask;
            foreach (var memory in memories.Take(query.Limit))
                yield return memory;
        }

        public async IAsyncEnumerable<MemoryEvent> TraceAsync(
            MemoryId id,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}