// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

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
