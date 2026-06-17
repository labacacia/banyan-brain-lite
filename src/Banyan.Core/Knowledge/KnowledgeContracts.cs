// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core.Isolation;

namespace Banyan.Core.Knowledge;

/// <summary>Where a recalled item came from — carried end-to-end for provenance/compliance (KB-1).</summary>
public sealed record KnowledgeSource(string Kind, string? SourceId = null, string? Detail = null)
{
    public const string NativeMemory = "native_memory";
    public const string Pack = "pack";
    public const string Evidence = "evidence";
}

/// <summary>A query against the knowledge base. Scope/permission come from the <see cref="IsolationContext"/>.</summary>
public sealed record KnowledgeQuery(string Text, int K = 10, SearchMode Mode = SearchMode.Hybrid);

/// <summary>One candidate returned by a single provider, in best-first order.</summary>
public sealed record Candidate(
    string Id,
    string Content,
    double Score,
    KnowledgeSource Source,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>A fused result across providers; <see cref="Sources"/> lists every provider that surfaced it.</summary>
public sealed record RankedResult(
    string Id,
    string Content,
    double Score,
    IReadOnlyList<KnowledgeSource> Sources,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record SearchResult(IReadOnlyList<RankedResult> Items);

/// <summary>What a provider can do — lets the retriever reason about coverage.</summary>
public sealed record KnowledgeProviderCaps(bool Lexical, bool Vector, bool Provenance = true);

/// <summary>
/// A pluggable knowledge source (KB-1): native memory, a mounted pack, or the
/// evidence pipeline. The retriever fans out to every registered provider; each
/// applies its own retrieval and returns provenance-tagged candidates already
/// scoped to the caller via the <see cref="IsolationContext"/>.
/// </summary>
public interface IKnowledgeProvider
{
    string Kind { get; }
    KnowledgeProviderCaps Caps { get; }
    ValueTask<IReadOnlyList<Candidate>> RetrieveAsync(
        IsolationContext ctx, KnowledgeQuery query, CancellationToken ct = default);
}

/// <summary>Optional reranking stage (cross-encoder etc.); editions wire a real one, default is none.</summary>
public interface IReranker
{
    ValueTask<IReadOnlyList<RankedResult>> RerankAsync(
        KnowledgeQuery query, IReadOnlyList<RankedResult> items, CancellationToken ct = default);
}

/// <summary>
/// Single hybrid-retrieval seam shared by every transport and edition (KB-1) — the
/// thing that removes "NPS runs text-only while HTTP runs hybrid". Orchestrates
/// fan-out across providers, RRF fusion, optional rerank, and top-K.
/// </summary>
public interface IHybridRetriever
{
    ValueTask<SearchResult> SearchAsync(
        IsolationContext ctx, KnowledgeQuery query, CancellationToken ct = default);
}
