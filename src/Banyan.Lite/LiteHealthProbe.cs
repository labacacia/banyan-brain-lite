// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;

namespace Banyan.Lite;

public sealed record LiteHealthReport(string Status, IReadOnlyDictionary<string, LiteHealthCheck> Checks);

public sealed record LiteHealthCheck(
    string Status,
    string? Message = null,
    string? ModelId = null,
    int? Dimensions = null);

public static class LiteHealthProbe
{
    public static async Task<LiteHealthReport> CheckAsync(
        SqliteMemoryStore store,
        IEmbedder embedder,
        CancellationToken ct = default)
    {
        var checks = new Dictionary<string, LiteHealthCheck>(StringComparer.Ordinal)
        {
            ["sqlite"] = await CheckSqliteAsync(store, ct),
            ["embedder"] = await CheckEmbedderAsync(embedder, ct),
        };

        var status = checks.Values.All(c => c.Status == "ok") ? "ok" : "degraded";
        return new LiteHealthReport(status, checks);
    }

    private static async Task<LiteHealthCheck> CheckSqliteAsync(SqliteMemoryStore store, CancellationToken ct)
    {
        try
        {
            await store.PingAsync(ct);
            return new LiteHealthCheck("ok");
        }
        catch (Exception ex)
        {
            return new LiteHealthCheck("degraded", ex.Message);
        }
    }

    private static async Task<LiteHealthCheck> CheckEmbedderAsync(IEmbedder embedder, CancellationToken ct)
    {
        try
        {
            var vector = await embedder.EmbedQueryAsync("banyan health probe", ct);
            if (vector.Length != embedder.Dimensions)
                return new LiteHealthCheck(
                    "degraded",
                    $"Embedder returned {vector.Length} dimensions; expected {embedder.Dimensions}.",
                    embedder.ModelId,
                    embedder.Dimensions);

            return new LiteHealthCheck("ok", ModelId: embedder.ModelId, Dimensions: embedder.Dimensions);
        }
        catch (Exception ex)
        {
            return new LiteHealthCheck("degraded", ex.Message, embedder.ModelId, embedder.Dimensions);
        }
    }
}
