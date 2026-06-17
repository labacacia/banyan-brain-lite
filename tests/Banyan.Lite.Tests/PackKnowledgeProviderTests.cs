// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Core.Isolation;
using Banyan.Core.Knowledge;
using Banyan.Lite;
using Xunit;

namespace Banyan.Lite.Tests;

public class PackKnowledgeProviderTests
{
    private static IsolationContext Ctx() =>
        IsolationContext.Local("default", new PrincipalRef("urn:nps:agent:t", "agent",
            new HashSet<string>(StringComparer.Ordinal)));

    // Embeds by simple keyword presence so cosine is deterministic in tests.
    private sealed class KeywordEmbedder(params string[] vocab) : IEmbedder
    {
        public int Dimensions => vocab.Length;
        public string ModelId => "test-keyword";
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[vocab.Length];
            for (var i = 0; i < vocab.Length; i++)
                v[i] = text.Contains(vocab[i], StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
            return ValueTask.FromResult(v);
        }
        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task VectorMatch_RanksSemanticallyClosestFirst()
    {
        var embedder = new KeywordEmbedder("banana", "rocket", "ocean");
        var records = new[]
        {
            new PackKnowledgeProvider.Record("r1", "a yellow banana fruit"),
            new PackKnowledgeProvider.Record("r2", "a rocket to the moon"),
        };
        var vectors = new Dictionary<string, float[]>
        {
            ["r1"] = (await embedder.EmbedAsync("banana")),
            ["r2"] = (await embedder.EmbedAsync("rocket")),
        };
        var provider = new PackKnowledgeProvider("pack-1", records, vectors, embedder);

        var hits = await provider.RetrieveAsync(Ctx(), new KnowledgeQuery("banana", Mode: SearchMode.Vector));
        Assert.Equal("r1", hits[0].Id);
        Assert.Equal(KnowledgeSource.Pack, hits[0].Source.Kind);
        Assert.Equal("pack-1", hits[0].Source.SourceId);
        Assert.Equal("r1", hits[0].Source.Detail);
    }

    [Fact]
    public async Task LexicalFallback_WhenNoVectors()
    {
        var embedder = new KeywordEmbedder("x");
        var records = new[] { new PackKnowledgeProvider.Record("r1", "the quick brown fox") };
        var provider = new PackKnowledgeProvider("p", records, new Dictionary<string, float[]>(), embedder);

        var hits = await provider.RetrieveAsync(Ctx(), new KnowledgeQuery("quick fox"));
        Assert.Single(hits);
        Assert.True(hits[0].Score > 0);
    }

    [Fact]
    public async Task FusesWithNativeProvider_ViaHybridRetriever()
    {
        var embedder = new KeywordEmbedder("banana");
        var records = new[] { new PackKnowledgeProvider.Record("p1", "banana pack note") };
        var vectors = new Dictionary<string, float[]> { ["p1"] = await embedder.EmbedAsync("banana") };
        var pack = new PackKnowledgeProvider("pack-1", records, vectors, embedder);

        var native = new InlineProvider(new Candidate(
            "m1", "native banana memory", 1.0, new KnowledgeSource(KnowledgeSource.NativeMemory, "m1")));

        var retriever = new DefaultHybridRetriever(new IKnowledgeProvider[] { native, pack });
        var result = await retriever.SearchAsync(Ctx(), new KnowledgeQuery("banana"));

        Assert.Contains(result.Items, i => i.Sources.Any(s => s.Kind == KnowledgeSource.Pack));
        Assert.Contains(result.Items, i => i.Sources.Any(s => s.Kind == KnowledgeSource.NativeMemory));
    }

    private sealed class InlineProvider(params Candidate[] items) : IKnowledgeProvider
    {
        public string Kind => KnowledgeSource.NativeMemory;
        public KnowledgeProviderCaps Caps => new(Lexical: true, Vector: true);
        public ValueTask<IReadOnlyList<Candidate>> RetrieveAsync(
            IsolationContext ctx, KnowledgeQuery q, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<Candidate>>(items);
    }
}
