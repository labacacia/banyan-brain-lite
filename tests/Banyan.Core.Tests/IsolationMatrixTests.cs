using Banyan.Core;
using Banyan.Core.Isolation;
using Xunit;

namespace Banyan.Core.Tests;

/// <summary>
/// ISO-8 — the isolation regression matrix from <c>plan/01-data-isolation.md §D</c>.
///
/// The three editions are separate solutions and cannot share an integration harness,
/// but Lite and Pro share <see cref="DefaultIsolationEnforcer"/> and Ent mirrors its
/// logic (ISO-2). This class pins every matrix cell that is expressible at the shared
/// contract level. Transport/edition-specific cells are covered where they live, and
/// mapped here so the whole matrix stays legible:
///
/// | Cell (§D)                          | Covered by                                                        |
/// |------------------------------------|-------------------------------------------------------------------|
/// | no NID → deny write                | Lite: LiteIsolationTests / Pro: TenantScopeTests.AnonymousRequest |
/// | missing capability → 403           | THIS + LiteIsolationTests + ProIsolationTests                     |
/// | cross-namespace read → deny        | THIS (AssertNamespaceReadable / ApplyScope)                       |
/// | cross-namespace write → deny       | THIS (AssertNamespaceWritable)                                    |
/// | cross-org/workspace read → deny    | Pro: ProIsolationTests / Ent: NpsGatewayTests                     |
/// | cross-tenant read → deny           | Ent: NpsGatewayTests.Resolver_rejects_payload_tenant_id_...       |
/// | envelope ≠ payload → deny          | THIS (AssertNoCrossBoundary) + Ent: NpsGatewayTests               |
/// | pool non-member access → deny      | Lite: MemoryPoolTests (IsMemberAsync) + MemoryEndpoints guard     |
/// </summary>
public class IsolationMatrixTests
{
    private static readonly DefaultIsolationEnforcer Enforcer = new();

    private static IsolationContext Ctx(string ns = "alice", string[]? caps = null, string[]? pools = null)
    {
        var capSet = new HashSet<string>(
            caps ?? new[] { IsolationCapabilities.MemoryRead, IsolationCapabilities.MemoryWrite },
            StringComparer.Ordinal);
        return IsolationContext.Local(ns, new PrincipalRef("urn:nps:agent:test", "agent", capSet), pools);
    }

    // ── missing capability → deny ───────────────────────────────────────────────
    [Theory]
    [InlineData("memory.write")]
    [InlineData("memory.read")]
    public void Matrix_MissingCapability_Denied(string capability)
    {
        var ctx = Ctx(caps: Array.Empty<string>());
        Assert.Throws<IsolationDeniedException>(() => Enforcer.RequireCapability(ctx, capability));
    }

    // ── cross-namespace read → deny ─────────────────────────────────────────────
    [Fact]
    public void Matrix_CrossNamespaceRead_Denied()
    {
        var ctx = Ctx(ns: "alice");
        Assert.Throws<IsolationDeniedException>(() => Enforcer.AssertNamespaceReadable(ctx, "bob"));
        Assert.Throws<IsolationDeniedException>(
            () => Enforcer.ApplyScope(ctx, new SearchQuery("q", Namespace: "bob")));
    }

    [Fact]
    public void Matrix_OwnAndPoolNamespaceRead_Allowed()
    {
        var ctx = Ctx(ns: "alice", pools: new[] { "pool:p1" });
        Enforcer.AssertNamespaceReadable(ctx, "alice");
        Enforcer.AssertNamespaceReadable(ctx, "pool:p1");
    }

    // ── cross-namespace write → deny ────────────────────────────────────────────
    [Fact]
    public void Matrix_CrossNamespaceWrite_Denied()
    {
        var ctx = Ctx(ns: "alice");
        Assert.Throws<IsolationDeniedException>(() => Enforcer.AssertNamespaceWritable(ctx, "bob"));
        Enforcer.AssertNamespaceWritable(ctx, "alice"); // own namespace allowed
    }

    // ── envelope/declared scope ≠ resolved context → deny ───────────────────────
    [Theory]
    [InlineData("tenant")]
    [InlineData("org")]
    [InlineData("workspace")]
    public void Matrix_DeclaredScopeMismatch_Denied(string layer)
    {
        var ctx = Ctx();
        Assert.Throws<IsolationDeniedException>(() => layer switch
        {
            "tenant"    => Throw(() => Enforcer.AssertNoCrossBoundary(ctx, declaredTenant: "other")),
            "org"       => Throw(() => Enforcer.AssertNoCrossBoundary(ctx, declaredOrg: "other")),
            "workspace" => Throw(() => Enforcer.AssertNoCrossBoundary(ctx, declaredWorkspace: "other")),
            _           => throw new InvalidOperationException(),
        });
    }

    [Fact]
    public void Matrix_DeclaredScopeMatches_Allowed()
    {
        var ctx = Ctx();
        Enforcer.AssertNoCrossBoundary(
            ctx,
            declaredTenant: IsolationDefaults.LocalTenant,
            declaredOrg: IsolationDefaults.LocalOrg,
            declaredWorkspace: IsolationDefaults.DefaultWorkspace);
    }

    private static bool Throw(Action a) { a(); return true; }
}
