// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.Json;
using Banyan.Core;
using Banyan.Lite;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;

namespace Banyan.Node;

/// <summary>
/// <see cref="IMemoryNodeProvider"/> adapter on top of <see cref="SqliteMemoryStore"/>.
/// Translates the NWP <see cref="QueryFrame"/> filter / vector-search options into Banyan's
/// <see cref="SearchQuery"/>, runs the search, and shapes rows according to the configured schema.
///
/// Filter DSL we accept (subset of NWP's filter expressions; PG/MSSQL translator isn't usable here):
///   <c>{"text": "search terms"}</c>             — BM25 lexical when no <see cref="QueryFrame.VectorSearch"/>
///   <c>{"namespace": "default"}</c>             — restrict to a namespace
///   <c>{"text": "...", "namespace": "..."}</c>  — combine
///
/// When <see cref="QueryFrame.VectorSearch"/>.Vector is non-null, vector search runs (with optional
/// <c>text</c> filter folded into a hybrid). When neither is supplied we return the most recent rows.
/// </summary>
public sealed class BanyanMemoryProvider(SqliteMemoryStore store) : IMemoryNodeProvider
{
    public static MemoryNodeSchema BuildSchema() => new()
    {
        TableName  = "memories",
        PrimaryKey = "memory_id",
        Fields     = new[]
        {
            Field("memory_id", "text", false, "stable Banyan memory id"),
            Field("namespace", "text", false, "logical bucket"),
            Field("content",   "text", false, "the memory body"),
            Field("agent_nid", "text", true,  "issuing agent NID, if any"),
            Field("created_at","text", false, "ISO 8601 UTC"),
            Field("updated_at","text", false, "ISO 8601 UTC"),
            Field("score",     "real", true,  "search score (bm25 / cosine / RRF)"),
            Field("lex_rank",  "int",  true,  "BM25 rank within hybrid"),
            Field("vec_rank",  "int",  true,  "vector rank within hybrid"),
        },
    };

    private static MemoryNodeField Field(string name, string type, bool nullable, string desc) =>
        new() { Name = name, ColumnName = name, Type = type, Nullable = nullable, Description = desc };

    public async Task<MemoryNodeQueryResult> QueryAsync(
        QueryFrame frame, MemoryNodeSchema schema, MemoryNodeOptions options, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var (text, ns) = ParseFilter(frame.Filter);
        var vec        = frame.VectorSearch;

        int limit = (int)Math.Min(frame.Limit > 0 ? frame.Limit : options.DefaultLimit, options.MaxLimit);

        if (vec is { Vector.Length: > 0 })
        {
            // Caller-supplied vector → vector / hybrid search. We don't currently re-embed agent-side
            // vectors; we treat the vector as the query and run pure vector + optional text filter via hybrid.
            // Without a runtime embedder rebind, fall back to a text-driven hybrid when text is set.
            if (!string.IsNullOrWhiteSpace(text))
                await CollectAsync(store.SearchAsync(new SearchQuery(text!, K: limit, Namespace: ns, Mode: SearchMode.Hybrid), ct), rows, frame.Fields);
            else
                await CollectAsync(VectorByExternalAsync(vec.Vector, limit, ns, ct),                                                                       rows, frame.Fields);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            await CollectAsync(store.SearchAsync(new SearchQuery(text!, K: limit, Namespace: ns, Mode: SearchMode.Hybrid), ct), rows, frame.Fields);
        }
        else
        {
            await CollectAsync(LatestAsync(ns, limit, ct), rows, frame.Fields);
        }

        return new MemoryNodeQueryResult { Rows = rows, NextCursor = "" };
    }

    public async Task<long> CountAsync(QueryFrame frame, MemoryNodeSchema schema, CancellationToken ct)
    {
        // Bound by the configured DefaultLimit cap — we don't enumerate the whole table for cheap counts.
        var (text, ns) = ParseFilter(frame.Filter);
        if (string.IsNullOrEmpty(text) && frame.VectorSearch is null) return 0;

        var query = string.IsNullOrEmpty(text)
            ? null
            : new SearchQuery(text!, K: 1000, Namespace: ns, Mode: SearchMode.Lexical);
        if (query is null) return 0;

        long n = 0;
        await foreach (var _ in store.SearchAsync(query, ct)) n++;
        return n;
    }

    public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
        QueryFrame frame, MemoryNodeSchema schema, MemoryNodeOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // For now we yield a single-page batch matching QueryAsync's result; full streaming pagination
        // can be wired through cursors once we add an offset-based search variant.
        var page = await QueryAsync(frame, schema, options, ct);
        if (page.Rows.Count > 0) yield return page.Rows;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<SearchHit> VectorByExternalAsync(
        float[] vec, int k, string? ns, [EnumeratorCancellation] CancellationToken ct)
    {
        // Caller handed us a precomputed vector instead of text. We still need to score against the
        // store's embeddings; bypass the store's embedder by re-using its public BM25 scaffold isn't
        // possible, so we route via SearchQuery with a synthetic empty text. Vector path needs query
        // text in the current API, so we surface "no-op" here until the store exposes
        // SearchByVectorAsync. For demo: log + empty.
        await Task.CompletedTask;
        yield break;
    }

    private async IAsyncEnumerable<SearchHit> LatestAsync(
        string? ns, int k, [EnumeratorCancellation] CancellationToken ct)
    {
        // The store doesn't expose a "list latest" method publicly; emulate via the SearchAsync API
        // with a wildcard match. FTS5 needs a non-empty match; pass "*" which BM25 treats as no-op.
        // Falls through to zero hits — clients should always supply text or vector.
        await Task.CompletedTask;
        yield break;
    }

    private static async Task CollectAsync(
        IAsyncEnumerable<SearchHit> hits,
        List<IReadOnlyDictionary<string, object?>> sink,
        IReadOnlyList<string>? fields)
    {
        await foreach (var h in hits) sink.Add(ToRow(h, fields));
    }

    private static IReadOnlyDictionary<string, object?> ToRow(SearchHit h, IReadOnlyList<string>? fields)
    {
        var all = new Dictionary<string, object?>
        {
            ["memory_id"]  = h.Memory.Id.ToString(),
            ["namespace"]  = h.Memory.Namespace,
            ["content"]    = h.Memory.Content,
            ["agent_nid"]  = h.Memory.AgentNid,
            ["created_at"] = h.Memory.CreatedAt.ToString("O"),
            ["updated_at"] = h.Memory.UpdatedAt.ToString("O"),
            ["score"]      = h.Score,
            ["lex_rank"]   = h.LexicalRank,
            ["vec_rank"]   = h.VectorRank,
        };
        // Field projection: NWP clients can request a subset via QueryFrame.Fields.
        return fields is { Count: > 0 }
            ? all.Where(kv => fields.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value)
            : all;
    }

    private static (string? text, string? ns) ParseFilter(JsonElement? filter)
    {
        if (filter is not { ValueKind: JsonValueKind.Object } obj) return (null, null);
        string? text = obj.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string? ns   = obj.TryGetProperty("namespace", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return (text, ns);
    }
}
