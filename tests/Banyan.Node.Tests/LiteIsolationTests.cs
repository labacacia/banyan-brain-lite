// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Core.Isolation;
using Banyan.Node.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Banyan.Node.Tests;

public sealed class LiteIsolationTests
{
    private static readonly DefaultIsolationEnforcer Enforcer = new();

    private static DefaultHttpContext Anonymous() => new();

    private static DefaultHttpContext Authenticated(string nid = "urn:nps:agent:test")
    {
        var http = new DefaultHttpContext();
        http.Items[NidAuthenticationOptions.ContextKeyNid] = nid;
        // No IdentFrame stashed → resolved capability set is empty.
        return http;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    [Fact]
    public void Resolve_NoNid_ReturnsAnonymousPrincipal()
    {
        var ctx = LiteIsolation.Resolve(Anonymous());
        Assert.Same(PrincipalRef.Anonymous, ctx.Principal);
    }

    [Fact]
    public void Resolve_WithNid_ReturnsAuthenticatedPrincipal()
    {
        var ctx = LiteIsolation.Resolve(Authenticated("urn:nps:agent:alice"));
        Assert.NotSame(PrincipalRef.Anonymous, ctx.Principal);
        Assert.Equal("urn:nps:agent:alice", ctx.Principal.Nid);
    }

    [Fact]
    public void Authorize_Anonymous_PassesThrough()
    {
        // Anonymous callers are gated by NidAuthMode (middleware), not capabilities.
        var denied = LiteIsolation.Authorize(Anonymous(), Enforcer, IsolationCapabilities.MemoryWrite);
        Assert.Null(denied);
    }

    [Fact]
    public void Authorize_AuthenticatedWithoutWriteCapability_Returns403()
    {
        var denied = LiteIsolation.Authorize(Authenticated(), Enforcer, IsolationCapabilities.MemoryWrite);
        Assert.NotNull(denied);
        Assert.Equal(403, StatusOf(denied!));
    }

    [Fact]
    public void Authorize_AuthenticatedWithoutReadCapability_Returns403()
    {
        var denied = LiteIsolation.Authorize(Authenticated(), Enforcer, IsolationCapabilities.MemoryRead);
        Assert.NotNull(denied);
        Assert.Equal(403, StatusOf(denied!));
    }
}
