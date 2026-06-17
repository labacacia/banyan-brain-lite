// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core.Isolation;

namespace Banyan.Core.Knowledge;

/// <summary>
/// Default <see cref="IHybridRetriever"/> (KB-1): fan out to every provider,
/// fuse their ranked lists with Reciprocal Rank Fusion, merge provenance,
/// optionally rerank, then cap at K. Pure orchestration — embedding/lexical
/// retrieval live in the providers, so this is identical across editions and
/// transports.
/// </summary>
public sealed class DefaultHybridRetriever : IHybridRetriever
{
    private readonly IReadOnlyList<IKnowledgeProvider> _providers;
    private readonly IReranker? _reranker;
    private readonly int _rrfK;

    public DefaultHybridRetriever(
        IEnumerable<IKnowledgeProvider> providers, IReranker? reranker = null, int rrfK = 60)
    {
        _providers = providers.ToArray();
        _reranker = reranker;
        _rrfK = rrfK > 0 ? rrfK : 60;
    }

    public async ValueTask<SearchResult> SearchAsync(
        IsolationContext ctx, KnowledgeQuery query, CancellationToken ct = default)
    {
        // Fan out. A provider that throws/empties simply contributes nothing.
        var lists = new List<IReadOnlyList<Candidate>>(_providers.Count);
        foreach (var p in _providers)
            lists.Add(await p.RetrieveAsync(ctx, query, ct).ConfigureAwait(false));

        var fused = ReciprocalRankFuse(lists, _rrfK);

        if (_reranker is not null && fused.Count > 0)
            fused = (await _reranker.RerankAsync(query, fused, ct).ConfigureAwait(false)).ToList();

        if (query.K > 0 && fused.Count > query.K)
            fused = fused.GetRange(0, query.K);

        return new SearchResult(fused);
    }

    /// <summary>RRF: score(id) = Σ 1/(k + rank) over providers; provenance unioned across providers.</summary>
    public static List<RankedResult> ReciprocalRankFuse(
        IReadOnlyList<IReadOnlyList<Candidate>> lists, int k)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var content = new Dictionary<string, string>(StringComparer.Ordinal);
        var meta = new Dictionary<string, IReadOnlyDictionary<string, object?>?>(StringComparer.Ordinal);
        var sources = new Dictionary<string, List<KnowledgeSource>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var list in lists)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var c = list[rank];
                if (!scores.ContainsKey(c.Id))
                {
                    scores[c.Id] = 0;
                    content[c.Id] = c.Content;
                    meta[c.Id] = c.Metadata;
                    sources[c.Id] = new List<KnowledgeSource>();
                    order.Add(c.Id);
                }
                scores[c.Id] += 1.0 / (k + rank + 1);
                sources[c.Id].Add(c.Source);
            }
        }

        return order
            .Select(id => new RankedResult(id, content[id], scores[id], sources[id], meta[id]))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }
}
