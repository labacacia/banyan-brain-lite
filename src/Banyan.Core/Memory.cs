// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Banyan.Core;

public sealed record Memory(
    MemoryId Id,
    EventId LatestEventId,
    string Namespace,
    string Content,
    JsonDocument? Metadata,
    string? AgentNid,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record MemoryEvent(
    EventId Id,
    MemoryId MemoryId,
    MemoryEventType Type,
    string? Content,
    JsonDocument? Metadata,
    string? AgentNid,
    string Namespace,
    DateTimeOffset OccurredAt
);

public enum MemoryEventType { Write, Update, Tombstone }
