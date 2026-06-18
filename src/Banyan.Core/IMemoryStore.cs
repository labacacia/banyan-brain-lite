// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Core;

public interface IMemoryStore : IAsyncDisposable
{
    Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default);
    Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default);
    Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default);
    Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default);
    Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default);
    IAsyncEnumerable<SearchHit> SearchAsync(SearchQuery query, CancellationToken ct = default);
    IAsyncEnumerable<MemoryEvent> TraceAsync(MemoryId id, CancellationToken ct = default);
}
