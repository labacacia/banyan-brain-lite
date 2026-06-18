// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Banyan.Core;
using Banyan.Core.Isolation;
using Xunit;

namespace Banyan.Core.Tests;

public class ScopedMemoryStoreTests
{
    private static readonly DefaultIsolationEnforcer Enforcer = new();

    private static IsolationContext Ctx(string ns = "alice", string[]? caps = null, string[]? pools = null)
    {
        var capSet = new HashSet<string>(
            caps ?? new[] { IsolationCapabilities.MemoryRead, IsolationCapabilities.MemoryWrite },
            StringComparer.Ordinal);
        return IsolationContext.Local(ns, new PrincipalRef("urn:nps:agent:test", "agent", capSet), pools);
    }

    private static ScopedMemoryStore Scoped(FakeStore inner, IsolationContext ctx)
        => new(inner, Enforcer, ctx);

    [Fact]
    public async Task Write_WithoutWriteCapability_Throws()
    {
        var store = Scoped(new FakeStore(), Ctx(caps: new[] { IsolationCapabilities.MemoryRead }));
        await Assert.ThrowsAsync<IsolationDeniedException>(
            () => store.WriteAsync(new WriteRequest("hi", Namespace: "alice")));
    }

    [Fact]
    public async Task Write_ToForeignNamespace_Throws()
    {
        var store = Scoped(new FakeStore(), Ctx(ns: "alice"));
        await Assert.ThrowsAsync<IsolationDeniedException>(
            () => store.WriteAsync(new WriteRequest("hi", Namespace: "bob")));
    }

    [Fact]
    public async Task Write_ToOwnNamespace_Delegates()
    {
        var inner = new FakeStore();
        var store = Scoped(inner, Ctx(ns: "alice"));
        await store.WriteAsync(new WriteRequest("hi", Namespace: "alice"));
        Assert.Equal(1, inner.Writes);
    }

    [Fact]
    public async Task Get_ForeignNamespaceMemory_ReturnsNull()
    {
        var inner = new FakeStore();
        var id = inner.Seed("bob", "secret");
        var store = Scoped(inner, Ctx(ns: "alice"));
        Assert.Null(await store.GetAsync(id));
    }

    [Fact]
    public async Task Get_OwnNamespaceMemory_Returns()
    {
        var inner = new FakeStore();
        var id = inner.Seed("alice", "mine");
        var store = Scoped(inner, Ctx(ns: "alice"));
        var got = await store.GetAsync(id);
        Assert.NotNull(got);
        Assert.Equal("mine", got!.Content);
    }

    [Fact]
    public async Task Recall_FiltersForeignNamespaces()
    {
        var inner = new FakeStore();
        var mine = inner.Seed("alice", "a");
        var theirs = inner.Seed("bob", "b");
        var store = Scoped(inner, Ctx(ns: "alice"));
        var got = await store.RecallAsync(new[] { mine, theirs });
        Assert.Single(got);
        Assert.Equal("a", got[0].Content);
    }

    [Fact]
    public async Task Search_AppliesScope_PassesAllowedNamespacesToInner()
    {
        var inner = new FakeStore();
        var store = Scoped(inner, Ctx(ns: "alice", pools: new[] { "pool-a" }));
        await foreach (var _ in store.SearchAsync(new SearchQuery("q"))) { }
        Assert.Equal(new[] { "alice", "pool-a" }, inner.LastSearch!.Namespaces);
    }

    [Fact]
    public async Task Forget_ForeignNamespaceMemory_Throws()
    {
        var inner = new FakeStore();
        var id = inner.Seed("bob", "secret");
        var store = Scoped(inner, Ctx(ns: "alice"));
        await Assert.ThrowsAsync<IsolationDeniedException>(() => store.ForgetAsync(id));
    }

    private sealed class FakeStore : IMemoryStore
    {
        private readonly Dictionary<MemoryId, Memory> _mem = new();
        public int Writes;
        public SearchQuery? LastSearch;

        public MemoryId Seed(string ns, string content)
        {
            var id = MemoryId.New();
            _mem[id] = new Memory(id, EventId.New(), ns, content, null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            return id;
        }

        public Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
        {
            Writes++;
            return Task.FromResult(MemoryId.New());
        }

        public Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
            => Task.FromResult(EventId.New());

        public Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(EventId.New());

        public Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
            => Task.FromResult(_mem.TryGetValue(id, out var m) ? m : null);

        public Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Memory>>(
                ids.Where(_mem.ContainsKey).Select(i => _mem[i]).ToArray());

        public async IAsyncEnumerable<SearchHit> SearchAsync(
            SearchQuery query, [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastSearch = query;
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<MemoryEvent> TraceAsync(
            MemoryId id, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
