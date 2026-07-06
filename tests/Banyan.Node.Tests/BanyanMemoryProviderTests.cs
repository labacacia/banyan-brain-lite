// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Banyan.Node;
using NPS.NWP.Frames;
using NPS.NWP.MemoryNode;
using Xunit;

namespace Banyan.Node.Tests;

public sealed class BanyanMemoryProviderTests : IAsyncLifetime
{
    private SqliteMemoryStore   _store    = null!;
    private BanyanMemoryProvider _provider = null!;
    private MemoryNodeOptions    _opts     = null!;

    public async ValueTask InitializeAsync()
    {
        _store    = await SqliteMemoryStore.OpenInMemoryAsync(new HashingEmbedder());
        _provider = new BanyanMemoryProvider(_store);
        _opts     = new MemoryNodeOptions
        {
            NodeId             = "test-node",
            DisplayName        = "Test",
            PathPrefix         = "/api/memory",
            DefaultLimit       = 10,
            MaxLimit           = 100,
            DefaultTokenBudget = 4096,
            Schema             = BanyanMemoryProvider.BuildSchema(),
        };

        await _store.WriteAsync(new WriteRequest("project deadline next Friday"));
        await _store.WriteAsync(new WriteRequest("the team meets Tuesday afternoons"));
        await _store.WriteAsync(new WriteRequest("BM25 search uses Okapi"));
        await _store.WriteAsync(new WriteRequest("agents authenticate via NID certificates"));
        await _store.WriteAsync(new WriteRequest("zebra crossing afternoon"));
    }
    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static QueryFrame Frame(JsonElement? filter, uint limit = 10, string[]? fields = null)
        => new()
        {
            Filter = filter,
            Limit  = limit,
            Fields = fields ?? Array.Empty<string>(),
            Order  = Array.Empty<QueryOrderClause>(),
        };

    private static JsonElement JsonObj(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Schema_AdvertisesExpectedFields()
    {
        var schema = BanyanMemoryProvider.BuildSchema();
        Assert.Equal("memories", schema.TableName);
        Assert.Equal("memory_id", schema.PrimaryKey);
        Assert.True(schema.HasField("content"));
        Assert.True(schema.HasField("namespace"));
        Assert.True(schema.HasField("score"));
    }

    [Fact]
    public async Task Query_TextFilter_FindsMatchingRows()
    {
        var frame  = Frame(JsonObj("""{"text":"deadline"}"""), limit: 5);
        var result = await _provider.QueryAsync(frame, _opts.Schema, _opts, default);

        Assert.NotEmpty(result.Rows);
        Assert.Contains(result.Rows, r => r["content"]?.ToString() == "project deadline next Friday");
    }

    [Fact]
    public async Task Query_FieldsProjection_OmitsUnrequested()
    {
        var frame  = Frame(JsonObj("""{"text":"deadline"}"""), limit: 5,
            fields: new[] { "memory_id", "content", "score" });
        var result = await _provider.QueryAsync(frame, _opts.Schema, _opts, default);

        Assert.NotEmpty(result.Rows);
        var first = result.Rows[0];
        Assert.True (first.ContainsKey("memory_id"));
        Assert.True (first.ContainsKey("content"));
        Assert.True (first.ContainsKey("score"));
        Assert.False(first.ContainsKey("namespace"));
        Assert.False(first.ContainsKey("created_at"));
    }

    [Fact]
    public async Task Query_NoFilter_ReturnsLatestRows()
    {
        var result = await _provider.QueryAsync(Frame(filter: null, limit: 2), _opts.Schema, _opts, default);
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.True(r.ContainsKey("memory_id")));
    }

    [Fact]
    public async Task Query_MetadataFilter_ReturnsMatchingLatestRows()
    {
        using var agentMeta = JsonDocument.Parse("""{"source":"agent"}""");
        using var browserMeta = JsonDocument.Parse("""{"source":"browser"}""");
        await _store.WriteAsync(new WriteRequest("metadata agent row", Metadata: agentMeta));
        await _store.WriteAsync(new WriteRequest("metadata browser row", Metadata: browserMeta));

        var result = await _provider.QueryAsync(
            Frame(JsonObj("""{"metadata":{"source":"agent"}}"""), limit: 10),
            _opts.Schema,
            _opts,
            default);

        Assert.Single(result.Rows);
        Assert.Equal("metadata agent row", result.Rows[0]["content"]);
    }

    [Fact]
    public async Task Query_TextAndMetadataFilter_FindsMatchingRows()
    {
        using var agentMeta = JsonDocument.Parse("""{"source":"agent"}""");
        using var browserMeta = JsonDocument.Parse("""{"source":"browser"}""");
        await _store.WriteAsync(new WriteRequest("metadata shared topic", Metadata: agentMeta));
        await _store.WriteAsync(new WriteRequest("metadata shared topic", Metadata: browserMeta));

        var result = await _provider.QueryAsync(
            Frame(JsonObj("""{"text":"metadata shared","metadata":{"source":"agent"}}"""), limit: 10),
            _opts.Schema,
            _opts,
            default);

        Assert.Single(result.Rows);
        Assert.Equal("metadata shared topic", result.Rows[0]["content"]);
    }

    [Fact]
    public async Task Query_LimitIsRespected()
    {
        var frame  = Frame(JsonObj("""{"text":"the"}"""), limit: 2);
        var result = await _provider.QueryAsync(frame, _opts.Schema, _opts, default);
        Assert.True(result.Rows.Count <= 2);
    }

    [Fact]
    public async Task Stream_YieldsAtLeastOneBatch_WhenMatchesExist()
    {
        var frame = Frame(JsonObj("""{"text":"team"}"""), limit: 5);
        var batches = new List<int>();
        await foreach (var batch in _provider.StreamAsync(frame, _opts.Schema, _opts, default))
            batches.Add(batch.Count);

        Assert.NotEmpty(batches);
        Assert.True(batches.Sum() >= 1);
    }

    [Fact]
    public async Task Count_TextFilter_ReturnsPositive()
    {
        var n = await _provider.CountAsync(Frame(JsonObj("""{"text":"deadline"}""")), _opts.Schema, default);
        Assert.True(n >= 1);
    }

    [Fact]
    public async Task Count_MetadataFilter_ReturnsVisibleRows()
    {
        using var agentMeta = JsonDocument.Parse("""{"source":"agent"}""");
        using var browserMeta = JsonDocument.Parse("""{"source":"browser"}""");
        await _store.WriteAsync(new WriteRequest("metadata count agent", Metadata: agentMeta));
        await _store.WriteAsync(new WriteRequest("metadata count browser", Metadata: browserMeta));

        var n = await _provider.CountAsync(
            Frame(JsonObj("""{"metadata":{"source":"agent"}}""")),
            _opts.Schema,
            default);

        Assert.Equal(1, n);
    }

    [Fact]
    public async Task Count_NoFilter_ReturnsVisibleRows()
    {
        var n = await _provider.CountAsync(Frame(filter: null), _opts.Schema, default);
        Assert.Equal(5, n);
    }
}
