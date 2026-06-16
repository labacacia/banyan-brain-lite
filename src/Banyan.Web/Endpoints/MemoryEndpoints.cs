using System.Diagnostics;
using System.Text.Json;
using Banyan.Core;
using Banyan.Core.Isolation;
using Banyan.Lite;
using Banyan.Node.Auth;

namespace Banyan.Web.Endpoints;

public static class MemoryEndpoints
{
    public sealed record WriteBody(string Content, string? Namespace = null, string? AgentNid = null, JsonElement? Metadata = null);
    public sealed record WriteResponse(string MemoryId);
    public sealed record UpdateBody(string Content, string? AgentNid = null, JsonElement? Metadata = null);
    public sealed record EventResponse(string EventId);
    public sealed record ForgetBody(string? Reason = null);

    public sealed record SearchHitDto(
        string MemoryId, string Namespace, string Content,
        double Score, int? LexicalRank, int? VectorRank,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record MemoryDto(string MemoryId, string LatestEventId, string Namespace, string Content, JsonElement? Metadata, string? AgentNid, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record EventDto(string EventId, string MemoryId, int Type, string TypeName, string? Content, JsonElement? Metadata, string? AgentNid, string Namespace, DateTimeOffset OccurredAt);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/memory").WithTags("memory");

        g.MapPost("/", async (HttpContext ctx, WriteBody body, SqliteMemoryStore store, IIsolationEnforcer enforcer, IMemoryPoolRepository pools, SqliteAuditLog audit) =>
        {
            if (LiteIsolation.Authorize(ctx, enforcer, IsolationCapabilities.MemoryWrite) is { } denied)
                return denied;
            var ns = body.Namespace ?? "default";
            if (!await MayAccessNamespaceAsync(ctx, pools, ns, ctx.RequestAborted))
                return PoolForbidden(ns);
            JsonDocument? meta = body.Metadata.HasValue
                ? JsonDocument.Parse(body.Metadata.Value.GetRawText())
                : null;
            // Server-side NID (set by NidAuthenticationMiddleware) overrides any client-supplied
            // agentNid string — clients can't spoof identity when auth is enabled.
            var verifiedNid = ctx.Items[Banyan.Auth.NidAuthenticationOptions.ContextKeyNid] as string;
            var req = new WriteRequest(
                Content:   body.Content,
                Namespace: ns,
                AgentNid:  verifiedNid ?? body.AgentNid,
                Metadata:  meta);
            var id = await store.WriteAsync(req);
            await audit.AppendAsync(AuditActor(ctx), "memory.write", id.ToString(), "ok", metadata: $"ns={ns}", ct: ctx.RequestAborted);
            return Results.Ok(new WriteResponse(id.ToString()));
        });

        g.MapGet("/search", async (
            HttpContext ctx, string q, SqliteMemoryStore store, IIsolationEnforcer enforcer, IMemoryPoolRepository pools,
            BanyanLiteMetrics metrics, string? mode = null, string? @namespace = null, int k = 10) =>
        {
            if (LiteIsolation.Authorize(ctx, enforcer, IsolationCapabilities.MemoryRead) is { } denied)
                return denied;
            // Explicitly searching a pool namespace requires membership; non-pool searches are unaffected.
            if (@namespace is not null && !await MayAccessNamespaceAsync(ctx, pools, @namespace, ctx.RequestAborted))
                return PoolForbidden(@namespace);
            var resolvedMode = ParseMode(mode);
            var query = new SearchQuery(Text: q, Namespace: @namespace, K: k, Mode: resolvedMode);
            var hits  = new List<SearchHitDto>();
            var sw = Stopwatch.StartNew();
            await foreach (var h in store.SearchAsync(query))
            {
                hits.Add(new SearchHitDto(
                    h.Memory.Id.ToString(), h.Memory.Namespace, h.Memory.Content,
                    h.Score, h.LexicalRank, h.VectorRank,
                    h.Memory.CreatedAt, h.Memory.UpdatedAt));
            }
            metrics.RecordQuery(sw.Elapsed.TotalMilliseconds);
            return Results.Ok(new { mode = resolvedMode.ToString().ToLowerInvariant(), hits });
        });

        g.MapGet("/{id}", async (HttpContext ctx, string id, SqliteMemoryStore store, IIsolationEnforcer enforcer, IMemoryPoolRepository pools) =>
        {
            if (LiteIsolation.Authorize(ctx, enforcer, IsolationCapabilities.MemoryRead) is { } denied)
                return denied;
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            var m = await store.GetAsync(new MemoryId(guid));
            if (m is null) return Results.NotFound();
            // Don't leak pool memories to non-members: treat as not found.
            if (!await MayAccessNamespaceAsync(ctx, pools, m.Namespace, ctx.RequestAborted)) return Results.NotFound();

            JsonElement? meta = m.Metadata is null ? null : JsonDocument.Parse(m.Metadata.RootElement.GetRawText()).RootElement;
            return Results.Ok(new MemoryDto(
                m.Id.ToString(), m.LatestEventId.ToString(), m.Namespace, m.Content,
                meta, m.AgentNid, m.CreatedAt, m.UpdatedAt));
        });

        g.MapPut("/{id}", async (HttpContext ctx, string id, UpdateBody body, SqliteMemoryStore store, IIsolationEnforcer enforcer, IMemoryPoolRepository pools, SqliteAuditLog audit) =>
        {
            if (LiteIsolation.Authorize(ctx, enforcer, IsolationCapabilities.MemoryWrite) is { } denied)
                return denied;
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            var existing = await store.GetAsync(new MemoryId(guid));
            if (existing is not null && !await MayAccessNamespaceAsync(ctx, pools, existing.Namespace, ctx.RequestAborted))
                return PoolForbidden(existing.Namespace);
            JsonDocument? meta = body.Metadata.HasValue
                ? JsonDocument.Parse(body.Metadata.Value.GetRawText())
                : null;
            var verifiedNid = ctx.Items[Banyan.Auth.NidAuthenticationOptions.ContextKeyNid] as string;
            try
            {
                var ev = await store.UpdateAsync(new MemoryId(guid),
                    new UpdateRequest(body.Content, meta, verifiedNid ?? body.AgentNid));
                await audit.AppendAsync(AuditActor(ctx), "memory.update", id, "ok", ct: ctx.RequestAborted);
                return Results.Ok(new EventResponse(ev.ToString()));
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        g.MapDelete("/{id}", async (HttpContext ctx, string id, SqliteMemoryStore store, IIsolationEnforcer enforcer, IMemoryPoolRepository pools, SqliteAuditLog audit, string? reason = null) =>
        {
            if (LiteIsolation.Authorize(ctx, enforcer, IsolationCapabilities.MemoryWrite) is { } denied)
                return denied;
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            var existing = await store.GetAsync(new MemoryId(guid));
            if (existing is not null && !await MayAccessNamespaceAsync(ctx, pools, existing.Namespace, ctx.RequestAborted))
                return PoolForbidden(existing.Namespace);
            try
            {
                var ev = await store.ForgetAsync(new MemoryId(guid), reason);
                await audit.AppendAsync(AuditActor(ctx), "memory.forget", id, "ok", ct: ctx.RequestAborted);
                return Results.Ok(new EventResponse(ev.ToString()));
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        g.MapGet("/{id}/trace", async (HttpContext ctx, string id, SqliteMemoryStore store, IIsolationEnforcer enforcer, IMemoryPoolRepository pools) =>
        {
            if (LiteIsolation.Authorize(ctx, enforcer, IsolationCapabilities.MemoryRead) is { } denied)
                return denied;
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            var m = await store.GetAsync(new MemoryId(guid));
            if (m is not null && !await MayAccessNamespaceAsync(ctx, pools, m.Namespace, ctx.RequestAborted))
                return Results.NotFound();
            var events = new List<EventDto>();
            await foreach (var e in store.TraceAsync(new MemoryId(guid)))
            {
                JsonElement? meta = e.Metadata is null ? null : JsonDocument.Parse(e.Metadata.RootElement.GetRawText()).RootElement;
                events.Add(new EventDto(
                    e.Id.ToString(), e.MemoryId.ToString(),
                    (int)e.Type, e.Type.ToString(),
                    e.Content, meta, e.AgentNid, e.Namespace, e.OccurredAt));
            }
            return Results.Ok(events);
        });
    }

    // OBS-4: audit subject is the verified NID (set by NID auth), else anonymous.
    private static string AuditActor(HttpContext ctx)
        => ctx.Items[Banyan.Auth.NidAuthenticationOptions.ContextKeyNid] as string ?? "anonymous";

    // ISO-5: pool namespaces (pool:*) are member-only. Non-pool namespaces are not gated here.
    // Returns true when access is allowed (non-pool namespace, or an authenticated member).
    private static async Task<bool> MayAccessNamespaceAsync(
        HttpContext ctx, IMemoryPoolRepository pools, string? @namespace, CancellationToken ct)
    {
        if (!LiteMemoryPool.TryGetPoolId(@namespace, out var poolId))
            return true;
        var nid = ctx.Items[Banyan.Auth.NidAuthenticationOptions.ContextKeyNid] as string;
        return !string.IsNullOrEmpty(nid) && await pools.IsMemberAsync(poolId, nid, ct);
    }

    private static IResult PoolForbidden(string? @namespace)
        => Results.Json(
            new { error_code = "POOL-FORBIDDEN", message = $"not a member of pool namespace '{@namespace}'" },
            statusCode: StatusCodes.Status403Forbidden);

    private static SearchMode ParseMode(string? mode) => (mode ?? "hybrid").ToLowerInvariant() switch
    {
        "lexical" => SearchMode.Lexical,
        "vector"  => SearchMode.Vector,
        "hybrid"  => SearchMode.Hybrid,
        _         => SearchMode.Hybrid,
    };
}
