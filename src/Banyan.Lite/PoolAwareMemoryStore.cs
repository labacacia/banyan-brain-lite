// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Banyan.Core;

namespace Banyan.Lite;

public sealed class PoolAwareMemoryStore(
    IMemoryStore inner,
    IMemoryPoolRepository pools,
    string agentId) : IMemoryStore
{
    public Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
        => inner.WriteAsync(req, ct);

    public Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
        => inner.UpdateAsync(id, req, ct);

    public Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
        => inner.ForgetAsync(id, reason, ct);

    public Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
        => inner.GetAsync(id, ct);

    public Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
        => inner.RecallAsync(ids, ct);

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var namespaces = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Namespace))
            namespaces.Add(query.Namespace);
        if (query.Namespaces is { Count: > 0 })
            namespaces.AddRange(query.Namespaces.Where(ns => !string.IsNullOrWhiteSpace(ns)));

        var bindings = await pools.ListBindingsAsync(agentId, ct);
        namespaces.AddRange(bindings.Select(b => LiteMemoryPool.Namespace(b.PoolId)));
        namespaces = namespaces.Distinct(StringComparer.Ordinal).ToList();

        if (namespaces.Count == 0)
        {
            await foreach (var hit in inner.SearchAsync(query, ct))
                yield return hit;
            yield break;
        }

        var seen = new HashSet<MemoryId>();
        var fanoutQuery = query with { Namespace = null, Namespaces = namespaces };
        await foreach (var hit in inner.SearchAsync(fanoutQuery, ct))
        {
            if (seen.Add(hit.Memory.Id))
                yield return hit;
        }
    }

    public async IAsyncEnumerable<Memory> ListAsync(
        MemoryListQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var namespaces = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Namespace))
            namespaces.Add(query.Namespace);
        if (query.Namespaces is { Count: > 0 })
            namespaces.AddRange(query.Namespaces.Where(ns => !string.IsNullOrWhiteSpace(ns)));

        var bindings = await pools.ListBindingsAsync(agentId, ct);
        namespaces.AddRange(bindings.Select(b => LiteMemoryPool.Namespace(b.PoolId)));
        namespaces = namespaces.Distinct(StringComparer.Ordinal).ToList();

        if (namespaces.Count == 0)
        {
            await foreach (var memory in inner.ListAsync(query, ct))
                yield return memory;
            yield break;
        }

        var seen = new HashSet<MemoryId>();
        var fanoutQuery = query with { Namespace = null, Namespaces = namespaces };
        await foreach (var memory in inner.ListAsync(fanoutQuery, ct))
        {
            if (seen.Add(memory.Id))
                yield return memory;
        }
    }

    public IAsyncEnumerable<MemoryEvent> TraceAsync(MemoryId id, CancellationToken ct = default)
        => inner.TraceAsync(id, ct);

    public async ValueTask DisposeAsync()
    {
        await pools.DisposeAsync();
        await inner.DisposeAsync();
    }
}
