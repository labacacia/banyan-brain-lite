// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Banyan.Core;
using Banyan.Embedders;
using Xunit;

namespace Banyan.Lite.Tests;

public sealed class HybridSearchTests : IAsyncLifetime
{
    private SqliteMemoryStore _store = null!;

    public async ValueTask InitializeAsync()
        => _store = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder());

    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private sealed class TieBreakEmbedder : IEmbedder
    {
        public int Dimensions => 2;
        public string ModelId => "tie-break";

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => ValueTask.FromResult(VectorForDocument(text));

        public ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
            => ValueTask.FromResult(new[] { 1f, 0f });

        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => ValueTask.FromResult(texts.Select(VectorForDocument).ToArray());

        private static float[] VectorForDocument(string text) => text switch
        {
            var s when s.Contains("semantic newer", StringComparison.Ordinal) => [1f, 0f],
            var s when s.Contains("semantic filler", StringComparison.Ordinal) => [0.5f, 0f],
            _ => [0f, 1f],
        };
    }

    private async Task SeedAsync()
    {
        await _store.WriteAsync(new WriteRequest("project deadline is March 15"));
        await _store.WriteAsync(new WriteRequest("the deadlines for migration"));
        await _store.WriteAsync(new WriteRequest("BM25 search uses Okapi formula"));
        await _store.WriteAsync(new WriteRequest("agents authenticate via NID certificates"));
        await _store.WriteAsync(new WriteRequest("zebra crossing afternoon"));
    }

    [Fact]
    public async Task HasEmbedder_True_WhenEmbedderProvided()
        => Assert.True(_store.HasEmbedder);

    [Fact]
    public async Task LexicalSearch_FindsExactWord()
    {
        await SeedAsync();
        var hits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("BM25", Mode: SearchMode.Lexical)))
            hits.Add(h);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Memory.Content.Contains("BM25"));
        Assert.All(hits, h => Assert.NotNull(h.LexicalRank));
        Assert.All(hits, h => Assert.Null(h.VectorRank));
    }

    [Fact]
    public async Task VectorSearch_FindsMorphologicalVariant()
    {
        await SeedAsync();
        var hits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("deadline", Mode: SearchMode.Vector)))
            hits.Add(h);

        Assert.NotEmpty(hits);
        // Vector search using char n-grams should pick up "deadlines" as well as "deadline".
        Assert.Contains(hits, h => h.Memory.Content.Contains("deadlines"));
        Assert.All(hits, h => Assert.NotNull(h.VectorRank));
        Assert.All(hits, h => Assert.Null(h.LexicalRank));
    }

    [Fact]
    public async Task VectorSearch_RespectsMetadataEquals_Filter()
    {
        using var agentMeta = JsonDocument.Parse("""{"source":"agent"}""");
        using var browserMeta = JsonDocument.Parse("""{"source":"browser"}""");
        await _store.WriteAsync(new WriteRequest("deadline metadata agent", Metadata: agentMeta));
        await _store.WriteAsync(new WriteRequest("deadline metadata browser", Metadata: browserMeta));

        var hits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery(
            "deadline",
            K: 10,
            Mode: SearchMode.Vector,
            MetadataEquals: new Dictionary<string, string> { ["source"] = "agent" })))
            hits.Add(h);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("agent", h.Memory.Metadata!.RootElement.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task HybridSearch_RespectsMetadataEquals_Filter()
    {
        using var agentMeta = JsonDocument.Parse("""{"source":"agent"}""");
        using var browserMeta = JsonDocument.Parse("""{"source":"browser"}""");
        await _store.WriteAsync(new WriteRequest("deadline metadata agent", Metadata: agentMeta));
        await _store.WriteAsync(new WriteRequest("deadline metadata browser", Metadata: browserMeta));

        var hits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery(
            "deadline",
            K: 10,
            Mode: SearchMode.Hybrid,
            MetadataEquals: new Dictionary<string, string> { ["source"] = "agent" })))
            hits.Add(h);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("agent", h.Memory.Metadata!.RootElement.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task HybridSearch_PopulatesBothRanks_WhenBothMatchSameDoc()
    {
        await SeedAsync();
        // "deadline" appears literally in one document, so it'll be in both rankers.
        var hits = new List<SearchHit>();
        await foreach (var h in _store.SearchAsync(new SearchQuery("deadline", Mode: SearchMode.Hybrid)))
            hits.Add(h);

        Assert.NotEmpty(hits);
        var topMatch = hits.First(h => h.Memory.Content.Contains("project deadline"));
        Assert.NotNull(topMatch.VectorRank);
        Assert.NotNull(topMatch.LexicalRank);
    }

    [Fact]
    public async Task HybridSearch_RrfTieBreaksByUpdatedAt()
    {
        await using var tuned = await SqliteMemoryStore.OpenInMemoryAsync(
            new TieBreakEmbedder(),
            retrieval: new RetrievalOptions(RrfK: 60, VectorTopK: 2, LexicalTopK: 2, FinalTopK: 2));

        await tuned.WriteAsync(new WriteRequest("alpha lexical older"));
        await Task.Delay(10);
        await tuned.WriteAsync(new WriteRequest("semantic newer"));
        await tuned.WriteAsync(new WriteRequest("semantic filler"));

        var hits = new List<SearchHit>();
        await foreach (var h in tuned.SearchAsync(new SearchQuery("alpha", K: 2, Mode: SearchMode.Hybrid)))
            hits.Add(h);

        Assert.Equal(2, hits.Count);
        Assert.Equal(hits[0].Score, hits[1].Score, precision: 12);
        Assert.Equal("semantic newer", hits[0].Memory.Content);
        Assert.Equal("alpha lexical older", hits[1].Memory.Content);
    }

    [Fact]
    public async Task HybridSearch_WithoutEmbedder_FallsBackToLexical()
    {
        await using var lexOnly = await SqliteMemoryStore.OpenInMemoryAsync(embedder: null);
        await lexOnly.WriteAsync(new WriteRequest("hello world"));

        Assert.False(lexOnly.HasEmbedder);

        var hits = new List<SearchHit>();
        await foreach (var h in lexOnly.SearchAsync(new SearchQuery("hello", Mode: SearchMode.Hybrid)))
            hits.Add(h);
        Assert.Single(hits);
        Assert.NotNull(hits[0].LexicalRank);
        Assert.Null(hits[0].VectorRank);
    }

    [Fact]
    public async Task HybridSearch_UsesRetrievalOptionsForFinalFallbackK()
    {
        await using var tuned = await SqliteMemoryStore.OpenInMemoryAsync(
            new HashingEmbedder(),
            retrieval: new RetrievalOptions(RrfK: 10, VectorTopK: 8, LexicalTopK: 8, FinalTopK: 2));
        await tuned.WriteAsync(new WriteRequest("alpha one"));
        await tuned.WriteAsync(new WriteRequest("alpha two"));
        await tuned.WriteAsync(new WriteRequest("alpha three"));

        var hits = new List<SearchHit>();
        await foreach (var h in tuned.SearchAsync(new SearchQuery("alpha", K: 0, Mode: SearchMode.Hybrid)))
            hits.Add(h);

        Assert.Equal(2, hits.Count);
    }
}
