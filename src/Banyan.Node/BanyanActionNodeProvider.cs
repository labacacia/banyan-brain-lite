// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Banyan.Core;
using NPS.NWP.ActionNode;
using NPS.NWP.Frames;

namespace Banyan.Node;

/// <summary>
/// NWP Action Node provider that exposes Banyan memory operations as NPS-2 actions.
/// Action IDs follow the {domain}.{verb} convention (NPS-2 §4.6):
///   memory.recall   — hybrid search
///   memory.remember — write a new memory
///   memory.update   — overwrite an existing memory
///   memory.forget   — delete a memory
/// </summary>
public sealed class BanyanActionNodeProvider(
    IMemoryStore store,
    ILogger<BanyanActionNodeProvider> logger) : IActionNodeProvider
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext ctx, CancellationToken ct)
    {
        logger.LogDebug("Act node: action={ActionId} agent={Agent}", frame.ActionId, ctx.AgentNid);

        return frame.ActionId switch
        {
            "memory.recall"   => await RecallAsync(frame, ctx, ct),
            "memory.remember" => await RememberAsync(frame, ctx, ct),
            "memory.update"   => await UpdateAsync(frame, ctx, ct),
            "memory.forget"   => await ForgetAsync(frame, ctx, ct),
            _ => throw new InvalidOperationException($"Unknown action id: '{frame.ActionId}'"),
        };
    }

    // ── memory.recall ─────────────────────────────────────────────────────

    private async Task<ActionExecutionResult> RecallAsync(
        ActionFrame frame, ActionContext ctx, CancellationToken ct)
    {
        var p         = frame.Params ?? throw new InvalidOperationException("memory.recall: params required.");
        var query     = Str(p, "query") ?? throw new InvalidOperationException("memory.recall: 'query' is required.");
        var ns        = Str(p, "namespace");
        var k         = p.TryGetProperty("k", out var kp) && kp.TryGetInt32(out var ki) ? ki : 5;
        var modeStr   = Str(p, "mode") ?? "hybrid";
        var mode      = modeStr.ToLowerInvariant() switch
        {
            "lexical" => SearchMode.Lexical,
            "vector"  => SearchMode.Vector,
            _         => SearchMode.Hybrid,
        };

        var hits = new List<object>();
        await foreach (var h in store.SearchAsync(new SearchQuery(query, k, ns, mode), ct))
        {
            hits.Add(new
            {
                memoryId  = h.Memory.Id.ToString(),
                @namespace = h.Memory.Namespace,
                content   = h.Memory.Content,
                score     = h.Score,
            });
        }

        var resultObj = new { count = hits.Count, hits };
        return new ActionExecutionResult
        {
            Result   = JsonSerializer.SerializeToElement(resultObj, _json),
            TokenEst = EstimateTokens(hits.Count > 0
                ? string.Join(" ", hits.Select(h => JsonSerializer.Serialize(h, _json)))
                : ""),
        };
    }

    // ── memory.remember ───────────────────────────────────────────────────

    private async Task<ActionExecutionResult> RememberAsync(
        ActionFrame frame, ActionContext ctx, CancellationToken ct)
    {
        var p       = frame.Params ?? throw new InvalidOperationException("memory.remember: params required.");
        var content = Str(p, "content") ?? throw new InvalidOperationException("memory.remember: 'content' is required.");
        var ns      = Str(p, "namespace") ?? "default";

        var id = await store.WriteAsync(new WriteRequest(content, ns, AgentNid: ctx.AgentNid), ct);

        return new ActionExecutionResult
        {
            Result   = JsonSerializer.SerializeToElement(new { memoryId = id.ToString(), @namespace = ns }, _json),
            TokenEst = 0,
        };
    }

    // ── memory.update ─────────────────────────────────────────────────────

    private async Task<ActionExecutionResult> UpdateAsync(
        ActionFrame frame, ActionContext ctx, CancellationToken ct)
    {
        var p        = frame.Params ?? throw new InvalidOperationException("memory.update: params required.");
        var idStr    = Str(p, "memoryId") ?? throw new InvalidOperationException("memory.update: 'memoryId' is required.");
        var content  = Str(p, "content")  ?? throw new InvalidOperationException("memory.update: 'content' is required.");

        if (!Guid.TryParse(idStr, out var guid))
            throw new InvalidOperationException($"memory.update: invalid memoryId '{idStr}'.");

        await store.UpdateAsync(new MemoryId(guid), new UpdateRequest(content, AgentNid: ctx.AgentNid), ct);

        return new ActionExecutionResult { Result = JsonSerializer.SerializeToElement(new { memoryId = idStr, updated = true }, _json) };
    }

    // ── memory.forget ─────────────────────────────────────────────────────

    private async Task<ActionExecutionResult> ForgetAsync(
        ActionFrame frame, ActionContext ctx, CancellationToken ct)
    {
        var p     = frame.Params ?? throw new InvalidOperationException("memory.forget: params required.");
        var idStr = Str(p, "memoryId") ?? throw new InvalidOperationException("memory.forget: 'memoryId' is required.");
        var reason = Str(p, "reason");

        if (!Guid.TryParse(idStr, out var guid))
            throw new InvalidOperationException($"memory.forget: invalid memoryId '{idStr}'.");

        await store.ForgetAsync(new MemoryId(guid), reason, ct);

        return new ActionExecutionResult { Result = JsonSerializer.SerializeToElement(new { memoryId = idStr, forgotten = true }, _json) };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string? Str(JsonElement el, string key) =>
        el.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static uint EstimateTokens(string text) =>
        (uint)Math.Max(0, text.Length / 4);

    /// <summary>Action registry declared in the NWM action list.</summary>
    public static Dictionary<string, ActionSpec> BuildActions() => new()
    {
        ["memory.recall"] = new ActionSpec
        {
            Description      = "Search stored Banyan memories by meaning and keywords. Returns ranked hits.",
            Async            = false,
            Idempotent       = true,
            TimeoutMsDefault = 10_000,
        },
        ["memory.remember"] = new ActionSpec
        {
            Description      = "Persist a new memory for future sessions. Returns the assigned memoryId.",
            Async            = false,
            Idempotent       = false,
            TimeoutMsDefault = 5_000,
        },
        ["memory.update"] = new ActionSpec
        {
            Description      = "Replace the content of an existing memory without changing its ID.",
            Async            = false,
            Idempotent       = true,
            TimeoutMsDefault = 5_000,
        },
        ["memory.forget"] = new ActionSpec
        {
            Description      = "Delete a stored memory by ID. The audit trace is preserved.",
            Async            = false,
            Idempotent       = true,
            TimeoutMsDefault = 5_000,
        },
    };
}
