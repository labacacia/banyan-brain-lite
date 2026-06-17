// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Core.Knowledge;
using Banyan.Core.KnowledgePacks;
using Banyan.Core.Isolation;

namespace Banyan.Lite;

/// <summary>
/// <see cref="IKnowledgeProvider"/> over one mounted <c>.banyanpack</c> (KB-3/KB-4):
/// scores each record by the better of in-pack vector similarity (cosine on the
/// embeddings the pack ships) and lexical token overlap, and tags every candidate
/// with <c>pack</c> provenance. The <see cref="IHybridRetriever"/> fuses this with
/// the native-memory provider, so packs become first-class semantic sources.
/// </summary>
public sealed class PackKnowledgeProvider : IKnowledgeProvider
{
    public sealed record Record(string Id, string Content);

    private readonly string _packId;
    private readonly IReadOnlyList<Record> _records;
    private readonly IReadOnlyDictionary<string, float[]> _vectors;
    private readonly IEmbedder _embedder;
    private readonly int _topN;

    public PackKnowledgeProvider(
        string packId,
        IReadOnlyList<Record> records,
        IReadOnlyDictionary<string, float[]> vectors,
        IEmbedder embedder,
        int topN = 50)
    {
        _packId = packId;
        _records = records;
        _vectors = vectors;
        _embedder = embedder;
        _topN = topN;
    }

    public string Kind => KnowledgeSource.Pack;
    public KnowledgeProviderCaps Caps => new(Lexical: true, Vector: _vectors.Count > 0);

    public async ValueTask<IReadOnlyList<Candidate>> RetrieveAsync(
        IsolationContext ctx, KnowledgeQuery query, CancellationToken ct = default)
    {
        var wantVector = _vectors.Count > 0 && query.Mode != SearchMode.Lexical;
        float[]? qv = wantVector ? await _embedder.EmbedQueryAsync(query.Text, ct).ConfigureAwait(false) : null;
        var queryTokens = Tokenize(query.Text);

        var scored = new List<Candidate>();
        foreach (var rec in _records)
        {
            double vec = 0;
            if (qv is not null && _vectors.TryGetValue(rec.Id, out var rv))
                vec = Math.Max(0, VectorMath.Cosine(qv, rv));

            double lex = query.Mode == SearchMode.Vector ? 0 : LexicalScore(queryTokens, rec.Content);

            var score = Math.Max(vec, lex);
            if (score <= 0) continue;

            scored.Add(new Candidate(
                rec.Id, rec.Content, score,
                new KnowledgeSource(KnowledgeSource.Pack, _packId, rec.Id)));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return _topN > 0 && scored.Count > _topN ? scored.GetRange(0, _topN) : scored;
    }

    private static double LexicalScore(HashSet<string> queryTokens, string content)
    {
        if (queryTokens.Count == 0) return 0;
        var docTokens = Tokenize(content);
        if (docTokens.Count == 0) return 0;
        var overlap = queryTokens.Count(docTokens.Contains);
        return (double)overlap / queryTokens.Count;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in text.Split(
            [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Length > 1) set.Add(tok);
        }
        return set;
    }
}
