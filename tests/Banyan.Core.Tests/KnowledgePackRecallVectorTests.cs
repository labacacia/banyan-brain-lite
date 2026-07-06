// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Banyan.Core;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public sealed class KnowledgePackRecallVectorTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"banyan-recall-{Guid.NewGuid():N}");

    public ValueTask InitializeAsync() { Directory.CreateDirectory(_dir); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { try { Directory.Delete(_dir, true); } catch { } return ValueTask.CompletedTask; }

    // Keyword embedder: deterministic vectors so in-pack cosine is exact.
    private sealed class KeywordEmbedder(params string[] vocab) : IEmbedder
    {
        public int Dimensions => vocab.Length;
        public string ModelId => "kw";
        public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var v = new float[vocab.Length];
            for (var i = 0; i < vocab.Length; i++)
                v[i] = text.Contains(vocab[i], StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
            return ValueTask.FromResult(v);
        }
        public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> t, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class EmptyStore : IMemoryStore
    {
        public Task<MemoryId> WriteAsync(WriteRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EventId> UpdateAsync(MemoryId id, UpdateRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default) => Task.FromResult<Memory?>(null);
        public Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Memory>>([]);
        public async IAsyncEnumerable<SearchHit> SearchAsync(SearchQuery q, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<Memory> ListAsync(MemoryListQuery q, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<MemoryEvent> TraceAsync(MemoryId id, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task<string> BuildV2PackAsync(IEmbedder embedder)
    {
        var records = new[]
        {
            new KnowledgePackMemoryRecord("r1", "s1", "text", "a rocket to the moon", "doc.md", 1.0),
            // r2's text does NOT contain "banana" (so lexical can't match it) but its stored
            // vector is the banana embedding — only the in-pack vector path can surface it.
            new KnowledgePackMemoryRecord("r2", "s1", "text", "tropical produce note", "doc.md", 1.0),
        };
        var vectors = new Dictionary<string, float[]>(StringComparer.Ordinal)
        {
            ["r1"] = await embedder.EmbedAsync("a rocket to the moon"),
            ["r2"] = await embedder.EmbedAsync("banana"),
        };

        // jsonl = one record per line (default options are single-line; [JsonPropertyName] still applies)
        var recordsJsonl = string.Join('\n',
            records.Select(r => JsonSerializer.Serialize(r))) + "\n";

        var manifest = new KnowledgePackManifest
        {
            PackId = "pk1", Name = "Vec", Version = "1.0.0", CreatedAt = DateTimeOffset.UnixEpoch,
            PackType = "knowledge", ContentTypes = ["text"], TargetScopes = ["default"],
            FormatVersion = 2, EmbedderProfile = embedder.ModelId,
        };
        var entries = new[]
        {
            new KnowledgePackArchiveEntry("memories/records.jsonl", Encoding.UTF8.GetBytes(recordsJsonl)),
            new KnowledgePackArchiveEntry(PackEmbeddings.Path, PackEmbeddings.Serialize(vectors)),
        };

        var path = Path.Combine(_dir, "vec.banyanpack");
        await using var fs = File.Create(path);
        await KnowledgePackArchive.WriteAsync(fs, manifest, entries);
        return path;
    }

    [Fact]
    public async Task Recall_UsesInPackVectors_ForSemanticMatch()
    {
        var embedder = new KeywordEmbedder("banana", "rocket", "ocean");
        var packPath = await BuildV2PackAsync(embedder);

        var registry = new FileKnowledgePackMountRegistry(Path.Combine(_dir, "registry.jsonl"));
        await registry.MountAsync(packPath, "default");

        var recall = new KnowledgePackRecallStore(new EmptyStore(), registry, embedder);

        var hits = new List<SearchHit>();
        await foreach (var h in recall.SearchAsync(new SearchQuery("banana", K: 10, Namespace: "default")))
            hits.Add(h);

        // "tropical produce note" has no literal "banana" — only the in-pack vector
        // can surface it, proving recall uses embeddings, not substring matching.
        var semantic = hits.SingleOrDefault(h => h.Memory.Content == "tropical produce note");
        Assert.NotNull(semantic);
        Assert.NotNull(semantic!.VectorRank);
    }
}
