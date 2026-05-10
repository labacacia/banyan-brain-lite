using Banyan.Core;
using Banyan.Embedders;
using Xunit;

namespace Banyan.Lite.Tests;

public sealed class UpdateForgetTests : IAsyncLifetime
{
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
        => _store = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder());

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    [Fact]
    public async Task Update_RewritesContent_AndAppendsEvent()
    {
        var id = await _store.WriteAsync(new WriteRequest("original content"));
        var updateEv = await _store.UpdateAsync(id, new UpdateRequest("revised content"));

        var m = await _store.GetAsync(id);
        Assert.NotNull(m);
        Assert.Equal("revised content", m!.Content);
        Assert.Equal(updateEv, m.LatestEventId);

        var trace = new List<MemoryEvent>();
        await foreach (var e in _store.TraceAsync(id)) trace.Add(e);
        Assert.Equal(2, trace.Count);
        Assert.Equal(MemoryEventType.Write,  trace[0].Type);
        Assert.Equal(MemoryEventType.Update, trace[1].Type);
    }

    [Fact]
    public async Task Update_RefreshesLexicalIndex()
    {
        var id = await _store.WriteAsync(new WriteRequest("originalkeyword zebra"));
        await _store.UpdateAsync(id, new UpdateRequest("newkeyword giraffe"));

        var oldHits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("originalkeyword", Mode: SearchMode.Lexical)))
            oldHits.Add(h);

        var newHits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("newkeyword", Mode: SearchMode.Lexical)))
            newHits.Add(h);

        Assert.Empty(oldHits);
        Assert.Single(newHits);
        Assert.Equal(id, newHits[0].Memory.Id);
    }

    [Fact]
    public async Task Update_RefreshesVectorIndex()
    {
        var id = await _store.WriteAsync(new WriteRequest("zebra crossing"));
        await _store.UpdateAsync(id, new UpdateRequest("project deadline"));

        var hits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("deadline", Mode: SearchMode.Vector)))
            hits.Add(h);

        Assert.Single(hits);
        Assert.Equal("project deadline", hits[0].Memory.Content);
    }

    [Fact]
    public async Task Forget_HidesFromSearch_ButKeepsTrace()
    {
        var id = await _store.WriteAsync(new WriteRequest("secret to forget"));
        var ev = await _store.ForgetAsync(id, "user-requested");

        Assert.Null(await _store.GetAsync(id));

        var lex = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("secret", Mode: SearchMode.Lexical)))
            lex.Add(h);
        Assert.Empty(lex);

        var vec = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("secret", Mode: SearchMode.Vector)))
            vec.Add(h);
        Assert.Empty(vec);

        // Trace still contains both events (write + tombstone) — audit invariant.
        var trace = new List<MemoryEvent>();
        await foreach (var e in _store.TraceAsync(id)) trace.Add(e);
        Assert.Equal(2, trace.Count);
        Assert.Equal(MemoryEventType.Write,     trace[0].Type);
        Assert.Equal(MemoryEventType.Tombstone, trace[1].Type);
        Assert.Equal(ev, trace[1].Id);
        Assert.Contains("user-requested", trace[1].Metadata!.RootElement.GetRawText());
    }

    [Fact]
    public async Task Update_OnUnknownId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.UpdateAsync(MemoryId.New(), new UpdateRequest("nope")));
    }

    [Fact]
    public async Task Forget_OnUnknownId_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ForgetAsync(MemoryId.New(), "x"));
    }
}
