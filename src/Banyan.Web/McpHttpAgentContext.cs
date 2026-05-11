// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Mcp;

namespace Banyan.Web;

internal sealed class McpHttpAgentContext(
    IHttpContextAccessor httpContextAccessor,
    LocalAgentIdentity localAgent) : IBanyanMcpAgentContext
{
    public string? CurrentAgentNid
        => httpContextAccessor.HttpContext?.Items[NidAuthenticationOptions.ContextKeyNid] as string
           ?? localAgent.Nid;
}
