// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core.Isolation;
using Banyan.Core.Knowledge;
using Xunit;

namespace Banyan.Core.Tests;

public class HybridRetrieverTests
{
    private static IsolationContext Ctx()
        => IsolationContext.Local("default", new PrincipalRef("urn:nps:agent:t", "agent",
            new HashSet<string>(StringComparer.Ordinal)));

    private sealed class FakeProvider(string kind, params Candidate[] items) : IKnowledgeProvider
    {
        public string Kind => kind;
        public KnowledgeProviderCaps Caps => new(Lexical: true, Vector: true);
        public ValueTask<IReadOnlyList<Candidate>> RetrieveAsync(
            IsolationContext ctx, KnowledgeQuery q, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<Candidate>>(items);
    }

    private static Candidate C(string id, string kind) =>
        new(id, $"content-{id}", 1.0, new KnowledgeSource(kind, id));

    [Fact]
    public async Task Fuses_AcrossProviders_AndMergesProvenance()
    {
        // "m1" appears in both providers → should rank first and carry both sources.
        var native = new FakeProvider(KnowledgeSource.NativeMemory,
            C("m1", KnowledgeSource.NativeMemory), C("m2", KnowledgeSource.NativeMemory));
        var pack = new FakeProvider(KnowledgeSource.Pack,
            C("m1", KnowledgeSource.Pack), C("p9", KnowledgeSource.Pack));

        var retriever = new DefaultHybridRetriever(new IKnowledgeProvider[] { native, pack });
        var result = await retriever.SearchAsync(Ctx(), new KnowledgeQuery("q", K: 10));

        Assert.Equal("m1", result.Items[0].Id);
        Assert.Equal(2, result.Items[0].Sources.Count); // native + pack
        Assert.Contains(result.Items[0].Sources, s => s.Kind == KnowledgeSource.Pack);
        Assert.Equal(3, result.Items.Count); // m1, m2, p9
    }

    [Fact]
    public async Task RespectsTopK()
    {
        var p = new FakeProvider(KnowledgeSource.NativeMemory,
            C("a", "n"), C("b", "n"), C("c", "n"), C("d", "n"));
        var retriever = new DefaultHybridRetriever(new IKnowledgeProvider[] { p });
        var result = await retriever.SearchAsync(Ctx(), new KnowledgeQuery("q", K: 2));
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task EmptyProviders_YieldEmptyResult()
    {
        var retriever = new DefaultHybridRetriever(new IKnowledgeProvider[] { new FakeProvider("x") });
        var result = await retriever.SearchAsync(Ctx(), new KnowledgeQuery("q"));
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Reranker_ReordersResults()
    {
        var p = new FakeProvider(KnowledgeSource.NativeMemory, C("a", "n"), C("b", "n"));
        var retriever = new DefaultHybridRetriever(
            new IKnowledgeProvider[] { p }, new ReverseReranker());
        var result = await retriever.SearchAsync(Ctx(), new KnowledgeQuery("q"));
        Assert.Equal("b", result.Items[0].Id); // reranker reversed
    }

    [Fact]
    public void Rrf_RanksMoreAgreedItemHigher()
    {
        var fused = DefaultHybridRetriever.ReciprocalRankFuse(
            new IReadOnlyList<Candidate>[]
            {
                new[] { C("x", "n"), C("shared", "n") },
                new[] { C("shared", "p"), C("y", "p") },
            }, k: 60);
        Assert.Equal("shared", fused[0].Id);
    }

    private sealed class ReverseReranker : IReranker
    {
        public ValueTask<IReadOnlyList<RankedResult>> RerankAsync(
            KnowledgeQuery q, IReadOnlyList<RankedResult> items, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<RankedResult>>(items.Reverse().ToList());
    }
}
