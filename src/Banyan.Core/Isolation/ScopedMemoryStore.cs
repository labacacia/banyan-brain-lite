// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

namespace Banyan.Core.Isolation;

/// <summary>
/// Decorates an <see cref="IMemoryStore"/> so every operation passes through the
/// <see cref="IIsolationEnforcer"/> first. Constructed per request once the
/// edition's <see cref="IIsolationContextResolver"/> has produced the
/// <see cref="IsolationContext"/>. All transports wrap the inner store with this
/// decorator at their DI seam (ISO-4/6), so no handler can reach the raw store.
/// </summary>
public sealed class ScopedMemoryStore : IMemoryStore
{
    private readonly IMemoryStore _inner;
    private readonly IIsolationEnforcer _enforcer;
    private readonly IsolationContext _ctx;

    public ScopedMemoryStore(IMemoryStore inner, IIsolationEnforcer enforcer, IsolationContext ctx)
    {
        _inner = inner;
        _enforcer = enforcer;
        _ctx = ctx;
    }

    public Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryWrite);
        _enforcer.AssertNamespaceWritable(_ctx, req.Namespace);
        return _inner.WriteAsync(req, ct);
    }

    public async Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryWrite);
        await AssertWritableTargetAsync(id, ct).ConfigureAwait(false);
        return await _inner.UpdateAsync(id, req, ct).ConfigureAwait(false);
    }

    public async Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryWrite);
        await AssertWritableTargetAsync(id, ct).ConfigureAwait(false);
        return await _inner.ForgetAsync(id, reason, ct).ConfigureAwait(false);
    }

    public async Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryRead);
        var memory = await _inner.GetAsync(id, ct).ConfigureAwait(false);
        if (memory is null)
            return null;
        // Do not leak existence of out-of-boundary memories: treat as not found.
        return _ctx.ReadableNamespaces().Contains(memory.Namespace, StringComparer.Ordinal) ? memory : null;
    }

    public async Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryRead);
        var all = await _inner.RecallAsync(ids, ct).ConfigureAwait(false);
        var readable = _ctx.ReadableNamespaces();
        return all.Where(m => readable.Contains(m.Namespace, StringComparer.Ordinal)).ToArray();
    }

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryRead);
        var scoped = _enforcer.ApplyScope(_ctx, query);
        await foreach (var hit in _inner.SearchAsync(scoped, ct).ConfigureAwait(false))
            yield return hit;
    }

    public async IAsyncEnumerable<MemoryEvent> TraceAsync(
        MemoryId id,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _enforcer.RequireCapability(_ctx, IsolationCapabilities.MemoryRead);
        await AssertReadableTargetAsync(id, ct).ConfigureAwait(false);
        await foreach (var evt in _inner.TraceAsync(id, ct).ConfigureAwait(false))
            yield return evt;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private async Task AssertWritableTargetAsync(MemoryId id, CancellationToken ct)
    {
        var existing = await _inner.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is not null)
            _enforcer.AssertNamespaceWritable(_ctx, existing.Namespace);
    }

    private async Task AssertReadableTargetAsync(MemoryId id, CancellationToken ct)
    {
        var existing = await _inner.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is not null)
            _enforcer.AssertNamespaceReadable(_ctx, existing.Namespace);
    }
}
