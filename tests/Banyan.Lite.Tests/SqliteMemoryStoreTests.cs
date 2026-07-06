// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Banyan.Core;
using Banyan.Lite;
using Xunit;

namespace Banyan.Lite.Tests;

public sealed class SqliteMemoryStoreTests : IAsyncLifetime
{
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
        => _store = await SqliteMemoryStore.OpenInMemoryAsync();

    public async ValueTask DisposeAsync()
        => await _store.DisposeAsync();

    // ── Write / Get ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_ReturnsDistinctIds()
    {
        var id1 = await _store.WriteAsync(new WriteRequest("memory one"));
        var id2 = await _store.WriteAsync(new WriteRequest("memory two"));
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Get_AfterWrite_ReturnsCorrectMemory()
    {
        var id = await _store.WriteAsync(new WriteRequest("hello world", Namespace: "test"));
        var m  = await _store.GetAsync(id);

        Assert.NotNull(m);
        Assert.Equal(id,       m.Id);
        Assert.Equal("hello world", m.Content);
        Assert.Equal("test",   m.Namespace);
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var result = await _store.GetAsync(MemoryId.New());
        Assert.Null(result);
    }

    [Fact]
    public async Task Get_DefaultNamespace_IsDefault()
    {
        var id = await _store.WriteAsync(new WriteRequest("no namespace given"));
        var m  = await _store.GetAsync(id);
        Assert.Equal("default", m!.Namespace);
    }

    // ── Recall ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recall_ReturnsOnlyFoundIds()
    {
        var id1 = await _store.WriteAsync(new WriteRequest("recall one"));
        var id2 = await _store.WriteAsync(new WriteRequest("recall two"));
        var ghost = MemoryId.New();

        var result = await _store.RecallAsync([id1, id2, ghost]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, m => m.Id == id1);
        Assert.Contains(result, m => m.Id == id2);
    }

    // List

    [Fact]
    public async Task List_ReturnsNewestFirst()
    {
        await _store.WriteAsync(new WriteRequest("older memory"));
        await Task.Delay(10);
        await _store.WriteAsync(new WriteRequest("newer memory"));

        var memories = await Collect(_store.ListAsync(new MemoryListQuery(Limit: 2)));

        Assert.Equal(2, memories.Count);
        Assert.Equal("newer memory", memories[0].Content);
        Assert.Equal("older memory", memories[1].Content);
    }

    [Fact]
    public async Task List_RespectsNamespaceAndLimit()
    {
        await _store.WriteAsync(new WriteRequest("alpha one", Namespace: "alpha"));
        await _store.WriteAsync(new WriteRequest("beta one", Namespace: "beta"));
        await Task.Delay(10);
        await _store.WriteAsync(new WriteRequest("alpha two", Namespace: "alpha"));

        var memories = await Collect(_store.ListAsync(new MemoryListQuery(Limit: 1, Namespace: "alpha")));

        Assert.Single(memories);
        Assert.Equal("alpha", memories[0].Namespace);
        Assert.Equal("alpha two", memories[0].Content);
    }

    [Fact]
    public async Task List_RespectsMetadataEquals_Filter()
    {
        using var stringMeta = JsonDocument.Parse("""{"source":"1","kind":"note"}""");
        using var numericMeta = JsonDocument.Parse("""{"source":1,"kind":"note"}""");
        await _store.WriteAsync(new WriteRequest("string metadata", Metadata: stringMeta));
        await _store.WriteAsync(new WriteRequest("numeric metadata", Metadata: numericMeta));

        var memories = await Collect(_store.ListAsync(new MemoryListQuery(
            Limit: 10,
            MetadataEquals: new Dictionary<string, string> { ["source"] = "1" })));

        Assert.Single(memories);
        Assert.Equal("string metadata", memories[0].Content);
    }

    // Search

    [Fact]
    public async Task Search_SingleToken_ReturnsMatchingMemories()
    {
        await _store.WriteAsync(new WriteRequest("the quick brown fox"));
        await _store.WriteAsync(new WriteRequest("lazy dog sleeping"));
        await _store.WriteAsync(new WriteRequest("fox jumped over fence"));

        var hits = await Collect(_store.SearchAsync(new SearchQuery("fox")));

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h =>
            Assert.Contains("fox", h.Memory.Content, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_MultiToken_RequiresAllTokens()
    {
        await _store.WriteAsync(new WriteRequest("cat sat on mat"));
        await _store.WriteAsync(new WriteRequest("cat chased dog"));
        await _store.WriteAsync(new WriteRequest("mat was wet"));

        // Both tokens must be present
        var hits = await Collect(_store.SearchAsync(new SearchQuery("cat mat")));

        Assert.Single(hits);
        Assert.Equal("cat sat on mat", hits[0].Memory.Content);
    }

    [Fact]
    public async Task Search_RespectsK_Limit()
    {
        for (var i = 0; i < 10; i++)
            await _store.WriteAsync(new WriteRequest($"banana smoothie recipe number {i}"));

        var hits = await Collect(_store.SearchAsync(new SearchQuery("banana", K: 3)));
        Assert.True(hits.Count <= 3);
    }

    [Fact]
    public async Task Search_RespectsNamespace_Filter()
    {
        await _store.WriteAsync(new WriteRequest("shared topic",   Namespace: "ns-a"));
        await _store.WriteAsync(new WriteRequest("shared topic",   Namespace: "ns-b"));
        await _store.WriteAsync(new WriteRequest("shared topic",   Namespace: "ns-a"));

        var hits = await Collect(_store.SearchAsync(new SearchQuery("shared", Namespace: "ns-a")));

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal("ns-a", h.Memory.Namespace));
    }

    [Fact]
    public async Task Search_RespectsMetadataEquals_Filter()
    {
        using var agentMeta = JsonDocument.Parse("""{"source":"agent","kind":"note"}""");
        using var browserMeta = JsonDocument.Parse("""{"source":"browser","kind":"note"}""");
        await _store.WriteAsync(new WriteRequest("shared topic", Metadata: agentMeta));
        await _store.WriteAsync(new WriteRequest("shared topic", Metadata: browserMeta));

        var hits = await Collect(_store.SearchAsync(new SearchQuery(
            "shared",
            Mode: SearchMode.Lexical,
            MetadataEquals: new Dictionary<string, string>
            {
                ["source"] = "agent",
                ["kind"] = "note",
            })));

        Assert.Single(hits);
        Assert.Equal("shared topic", hits[0].Memory.Content);
        Assert.Equal("agent", hits[0].Memory.Metadata!.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Search_ScoresArePositiveAndDescending()
    {
        await _store.WriteAsync(new WriteRequest("apple apple apple"));
        await _store.WriteAsync(new WriteRequest("apple banana"));

        var hits = await Collect(_store.SearchAsync(new SearchQuery("apple")));

        Assert.All(hits, h => Assert.True(h.Score > 0));

        for (var i = 1; i < hits.Count; i++)
            Assert.True(hits[i - 1].Score >= hits[i].Score, "scores should be descending");
    }

    [Fact]
    public async Task Search_Empty_ReturnsNothing()
    {
        await _store.WriteAsync(new WriteRequest("some content"));
        var hits = await Collect(_store.SearchAsync(new SearchQuery("   ")));
        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_100Items_Top5RelevantFirst()
    {
        // 97 off-topic memories
        for (var i = 0; i < 97; i++)
            await _store.WriteAsync(new WriteRequest($"unrelated entry about weather and traffic {i}"));

        // 3 on-topic
        await _store.WriteAsync(new WriteRequest("neural network training requires gradient descent"));
        await _store.WriteAsync(new WriteRequest("backpropagation is core to neural network learning"));
        await _store.WriteAsync(new WriteRequest("neural networks excel at pattern recognition tasks"));

        var hits = await Collect(_store.SearchAsync(new SearchQuery("neural network", K: 5)));

        Assert.True(hits.Count >= 3);
        var top3 = hits.Take(3).ToList();
        Assert.All(top3, h =>
            Assert.Contains("neural", h.Memory.Content, StringComparison.OrdinalIgnoreCase));
    }

    // ── Trace ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trace_AfterWrite_ReturnsSingleWriteEvent()
    {
        var id = await _store.WriteAsync(new WriteRequest("traceable content", AgentNid: "urn:nps:agent:local:test"));
        var events = await Collect(_store.TraceAsync(id));

        Assert.Single(events);
        var ev = events[0];
        Assert.Equal(MemoryEventType.Write, ev.Type);
        Assert.Equal(id,                    ev.MemoryId);
        Assert.Equal("traceable content",   ev.Content);
        Assert.Equal("urn:nps:agent:local:test", ev.AgentNid);
    }

    [Fact]
    public async Task Trace_UnknownId_ReturnsEmpty()
    {
        var events = await Collect(_store.TraceAsync(MemoryId.New()));
        Assert.Empty(events);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }
}
