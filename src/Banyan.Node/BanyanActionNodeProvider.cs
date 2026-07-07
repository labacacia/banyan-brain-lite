// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Ivy.ActNode;
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
    IEnumerable<IActActionHandler> handlers,
    ILogger<BanyanActionNodeProvider> logger) : IActionNodeProvider
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IActActionHandler> _handlers =
        handlers.ToDictionary(h => h.ActionName, StringComparer.OrdinalIgnoreCase);

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionFrame frame, ActionContext ctx, CancellationToken ct)
    {
        logger.LogDebug("Act node: action={ActionId} agent={Agent}", frame.ActionId, ctx.AgentNid);

        if (!_handlers.TryGetValue(frame.ActionId, out var handler))
            throw new InvalidOperationException($"Unknown action id: '{frame.ActionId}'");

        var result = await handler.InvokeAsync(new ActActionContext(
            Arguments: ToJsonNode(frame.Params),
            AgentId: ctx.AgentNid,
            SessionId: ctx.RequestId,
            WorkspaceId: null,
            Metadata: new Dictionary<string, string?>
            {
                ["priority"] = ctx.Priority,
                ["task_id"] = ctx.TaskId
            }
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase)), ct);

        var resultJson = result?.ToJsonString(_json) ?? "";
        return new ActionExecutionResult
        {
            Result = ToJsonElement(result),
            TokenEst = EstimateTokens(resultJson)
        };
    }

    private static JsonNode? ToJsonNode(JsonElement? element) =>
        element is null ? null : JsonNode.Parse(element.Value.GetRawText());

    private static JsonElement? ToJsonElement(JsonNode? node)
    {
        if (node is null)
            return null;
        using var doc = JsonDocument.Parse(node.ToJsonString(_json));
        return doc.RootElement.Clone();
    }

    private static uint EstimateTokens(string text) =>
        (uint)Math.Max(0, text.Length / 4);

    /// <summary>Action registry declared in the NWM action list.</summary>
    public static IReadOnlyDictionary<string, ActionSpec> BuildActions() =>
        BanyanActNodeActions.CreateNwpActionSpecs();
}
