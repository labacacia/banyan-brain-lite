// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Xunit;

namespace Banyan.Lite.Tests;

/// <summary>
/// Verifies the sqlite-vec ANN path produces results consistent with the linear-scan fallback.
/// Auto-skips when vec0.so / vocab / model aren't installed.
/// </summary>
public sealed class SqliteVecTests
{
    private static (bool ok, string libPath) VecAvailable()
    {
        var p = SqliteVecLoader.ResolvePath();
        return (p is not null, p ?? "");
    }

    private static bool ModelAvailable(out OnnxEmbedderOptions opts)
    {
        opts = new OnnxEmbedderOptions();
        if (Environment.GetEnvironmentVariable("BANYAN_EMBEDDER_MODEL") is { Length: > 0 } m) opts.ModelPath = m;
        if (Environment.GetEnvironmentVariable("BANYAN_EMBEDDER_VOCAB") is { Length: > 0 } v) opts.VocabPath = v;
        return File.Exists(EmbedderPaths.ExpandHome(opts.ModelPath))
            && File.Exists(EmbedderPaths.ExpandHome(opts.VocabPath));
    }

    [Fact]
    public async Task VecEnabled_True_WhenLibraryLoads()
    {
        var (ok, libPath) = VecAvailable();
        if (!ok) return;

        await using var store = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder(), libPath);
        Assert.True(store.VecEnabled);
    }

    [Fact]
    public async Task VecEnabled_False_WhenLibraryMissing()
    {
        await using var store = await SqliteMemoryStore.OpenInMemoryAsync(
            new HashingEmbedder(), sqliteVecLibPath: "/nonexistent/vec0.so");
        Assert.False(store.VecEnabled);
    }

    [Fact]
    public async Task VectorSearch_AnnAndLinear_AgreeOnTopHit()
    {
        var (ok, libPath) = VecAvailable();
        if (!ok || !ModelAvailable(out _)) return;

        // Two separate stores: one with vec0 enabled, one falling back to linear.
        await using var ann = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder(), libPath);
        await using var lin = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder(), sqliteVecLibPath: "/nope/vec0.so");
        Assert.True(ann.VecEnabled);
        Assert.False(lin.VecEnabled);

        string[] corpus =
        [
            "project deadline next Friday",
            "the team meets Tuesday afternoons",
            "BM25 search uses the Okapi formula",
            "agents authenticate via NID certificates",
            "I left my keys at the office",
            "zebra crossing in the afternoon",
        ];
        foreach (var c in corpus)
        {
            await ann.WriteAsync(new WriteRequest(c));
            await lin.WriteAsync(new WriteRequest(c));
        }

        async Task<string?> TopAsync(SqliteMemoryStore s, string q)
        {
            await foreach (var h in s.SearchAsync(new SearchQuery(q, K: 1, Mode: SearchMode.Vector)))
                return h.Memory.Content;
            return null;
        }

        // For deterministic HashingEmbedder, the top hit must match across both paths.
        foreach (var q in new[] { "deadline", "team", "BM25", "keys" })
        {
            Assert.Equal(await TopAsync(lin, q), await TopAsync(ann, q));
        }
    }

    [Fact]
    public async Task Forget_RemovesFromVecIndex()
    {
        var (ok, libPath) = VecAvailable();
        if (!ok) return;

        await using var store = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder(), libPath);
        var id = await store.WriteAsync(new WriteRequest("ephemeral note about the keys"));

        var before = new List<SearchHit>();
        await foreach (var h in store.SearchAsync(new SearchQuery("keys", Mode: SearchMode.Vector))) before.Add(h);
        Assert.NotEmpty(before);

        await store.ForgetAsync(id, "test");

        var after = new List<SearchHit>();
        await foreach (var h in store.SearchAsync(new SearchQuery("keys", Mode: SearchMode.Vector))) after.Add(h);
        Assert.Empty(after);
    }

    [Fact]
    public async Task Update_RefreshesVecIndex()
    {
        var (ok, libPath) = VecAvailable();
        if (!ok) return;

        await using var store = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder(), libPath);
        var id = await store.WriteAsync(new WriteRequest("alpha bravo"));
        await store.UpdateAsync(id, new UpdateRequest("charlie delta"));

        var hits = new List<SearchHit>();
        await foreach (var h in store.SearchAsync(new SearchQuery("delta", Mode: SearchMode.Vector))) hits.Add(h);

        Assert.NotEmpty(hits);
        Assert.Equal(id, hits[0].Memory.Id);
        Assert.Equal("charlie delta", hits[0].Memory.Content);
    }
}
