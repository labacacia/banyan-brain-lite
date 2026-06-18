// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Core;

public readonly record struct MemoryId(Guid Value)
{
    public static MemoryId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct EventId(Guid Value)
{
    // Guid v7 is timestamp-ordered — serves as ULID for ordering purposes
    public static EventId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}
