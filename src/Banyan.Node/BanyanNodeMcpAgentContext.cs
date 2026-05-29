// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Mcp;

namespace Banyan.Node;

/// <summary>
/// <see cref="IBanyanMcpAgentContext"/> for the HTTP MCP transport.
/// Reads the verified agent NID set by <see cref="NidAuthenticationOptions"/>
/// middleware from the current HTTP context.
/// </summary>
internal sealed class BanyanNodeMcpAgentContext(IHttpContextAccessor httpContextAccessor) : IBanyanMcpAgentContext
{
    public string? CurrentAgentNid
        => httpContextAccessor.HttpContext?.Items[NidAuthenticationOptions.ContextKeyNid] as string;
}
