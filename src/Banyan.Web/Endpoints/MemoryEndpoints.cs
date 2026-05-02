using System.Text.Json;
using Banyan.Core;
using Banyan.Lite;

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

        g.MapPost("/", async (HttpContext ctx, WriteBody body, SqliteMemoryStore store) =>
        {
            JsonDocument? meta = body.Metadata.HasValue
                ? JsonDocument.Parse(body.Metadata.Value.GetRawText())
                : null;
            // Server-side NID (set by NidAuthenticationMiddleware) overrides any client-supplied
            // agentNid string — clients can't spoof identity when auth is enabled.
            var verifiedNid = ctx.Items[Banyan.Auth.NidAuthenticationOptions.ContextKeyNid] as string;
            var req = new WriteRequest(
                Content:   body.Content,
                Namespace: body.Namespace ?? "default",
                AgentNid:  verifiedNid ?? body.AgentNid,
                Metadata:  meta);
            var id = await store.WriteAsync(req);
            return Results.Ok(new WriteResponse(id.ToString()));
        });

        g.MapGet("/search", async (
            string q, SqliteMemoryStore store,
            string? mode = null, string? @namespace = null, int k = 10) =>
        {
            var resolvedMode = ParseMode(mode);
            var query = new SearchQuery(Text: q, Namespace: @namespace, K: k, Mode: resolvedMode);
            var hits  = new List<SearchHitDto>();
            await foreach (var h in store.SearchAsync(query))
            {
                hits.Add(new SearchHitDto(
                    h.Memory.Id.ToString(), h.Memory.Namespace, h.Memory.Content,
                    h.Score, h.LexicalRank, h.VectorRank,
                    h.Memory.CreatedAt, h.Memory.UpdatedAt));
            }
            return Results.Ok(new { mode = resolvedMode.ToString().ToLowerInvariant(), hits });
        });

        g.MapGet("/{id}", async (string id, SqliteMemoryStore store) =>
        {
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            var m = await store.GetAsync(new MemoryId(guid));
            if (m is null) return Results.NotFound();

            JsonElement? meta = m.Metadata is null ? null : JsonDocument.Parse(m.Metadata.RootElement.GetRawText()).RootElement;
            return Results.Ok(new MemoryDto(
                m.Id.ToString(), m.LatestEventId.ToString(), m.Namespace, m.Content,
                meta, m.AgentNid, m.CreatedAt, m.UpdatedAt));
        });

        g.MapPut("/{id}", async (HttpContext ctx, string id, UpdateBody body, SqliteMemoryStore store) =>
        {
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            JsonDocument? meta = body.Metadata.HasValue
                ? JsonDocument.Parse(body.Metadata.Value.GetRawText())
                : null;
            var verifiedNid = ctx.Items[Banyan.Auth.NidAuthenticationOptions.ContextKeyNid] as string;
            try
            {
                var ev = await store.UpdateAsync(new MemoryId(guid),
                    new UpdateRequest(body.Content, meta, verifiedNid ?? body.AgentNid));
                return Results.Ok(new EventResponse(ev.ToString()));
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        g.MapDelete("/{id}", async (string id, SqliteMemoryStore store, string? reason = null) =>
        {
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
            try
            {
                var ev = await store.ForgetAsync(new MemoryId(guid), reason);
                return Results.Ok(new EventResponse(ev.ToString()));
            }
            catch (InvalidOperationException ex) { return Results.NotFound(new { error = ex.Message }); }
        });

        g.MapGet("/{id}/trace", async (string id, SqliteMemoryStore store) =>
        {
            if (!Guid.TryParse(id, out var guid)) return Results.BadRequest(new { error = "invalid id" });
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

    private static SearchMode ParseMode(string? mode) => (mode ?? "hybrid").ToLowerInvariant() switch
    {
        "lexical" => SearchMode.Lexical,
        "vector"  => SearchMode.Vector,
        "hybrid"  => SearchMode.Hybrid,
        _         => SearchMode.Hybrid,
    };
}
