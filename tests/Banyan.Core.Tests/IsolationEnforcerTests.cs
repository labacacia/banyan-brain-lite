// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Core.Isolation;
using Xunit;

namespace Banyan.Core.Tests;

public class IsolationEnforcerTests
{
    private static readonly DefaultIsolationEnforcer Enforcer = new();

    private static IsolationContext Ctx(
        string ns = "default",
        string[]? caps = null,
        string[]? pools = null)
    {
        var capSet = new HashSet<string>(caps ?? new[] { IsolationCapabilities.MemoryRead, IsolationCapabilities.MemoryWrite }, StringComparer.Ordinal);
        var principal = new PrincipalRef("urn:nps:agent:test", "agent", capSet);
        return IsolationContext.Local(ns, principal, pools);
    }

    [Fact]
    public void RequireCapability_Present_DoesNotThrow()
    {
        Enforcer.RequireCapability(Ctx(), IsolationCapabilities.MemoryWrite);
    }

    [Fact]
    public void RequireCapability_Missing_Throws()
    {
        var ctx = Ctx(caps: new[] { IsolationCapabilities.MemoryRead });
        var ex = Assert.Throws<IsolationDeniedException>(
            () => Enforcer.RequireCapability(ctx, IsolationCapabilities.MemoryWrite));
        Assert.Contains("memory.write", ex.Message);
    }

    [Fact]
    public void ApplyScope_NoNamespace_DefaultsToReadableNamespaces()
    {
        var ctx = Ctx(ns: "alice", pools: new[] { "pool-a" });
        var scoped = Enforcer.ApplyScope(ctx, new SearchQuery("q"));
        Assert.Equal(new[] { "alice", "pool-a" }, scoped.Namespaces);
    }

    [Fact]
    public void ApplyScope_RequestedInBoundary_Narrows()
    {
        var ctx = Ctx(ns: "alice", pools: new[] { "pool-a" });
        var scoped = Enforcer.ApplyScope(ctx, new SearchQuery("q", Namespace: "pool-a"));
        Assert.Equal("pool-a", scoped.Namespace);
    }

    [Fact]
    public void ApplyScope_RequestedOutsideBoundary_Throws()
    {
        var ctx = Ctx(ns: "alice");
        Assert.Throws<IsolationDeniedException>(
            () => Enforcer.ApplyScope(ctx, new SearchQuery("q", Namespace: "bob")));
    }

    [Fact]
    public void ApplyScope_RequestedNamespacesList_FiltersToAllowed()
    {
        var ctx = Ctx(ns: "alice", pools: new[] { "pool-a" });
        var q = new SearchQuery("q", Namespaces: new[] { "pool-a", "bob" });
        var scoped = Enforcer.ApplyScope(ctx, q);
        Assert.Equal(new[] { "pool-a" }, scoped.Namespaces);
    }

    [Fact]
    public void AssertNamespaceWritable_OwnNamespace_Ok()
    {
        Enforcer.AssertNamespaceWritable(Ctx(ns: "alice"), "alice");
    }

    [Fact]
    public void AssertNamespaceWritable_OtherNamespace_Throws()
    {
        Assert.Throws<IsolationDeniedException>(
            () => Enforcer.AssertNamespaceWritable(Ctx(ns: "alice"), "bob"));
    }

    [Fact]
    public void AssertNoCrossBoundary_MatchingDeclarations_Ok()
    {
        var ctx = Ctx();
        Enforcer.AssertNoCrossBoundary(ctx, declaredTenant: IsolationDefaults.LocalTenant);
    }

    [Fact]
    public void AssertNoCrossBoundary_MismatchedTenant_Throws()
    {
        var ctx = Ctx();
        Assert.Throws<IsolationDeniedException>(
            () => Enforcer.AssertNoCrossBoundary(ctx, declaredTenant: "other-tenant"));
    }
}
