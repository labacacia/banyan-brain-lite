// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.MemoryScopes;

public sealed record MemoryScopeContext(
    string TenantId = "default",
    string WorkspaceId = "default",
    string? AgentId = null,
    string? SessionId = null);
