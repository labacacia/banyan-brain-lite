// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using Banyan.Core;
using Ivy.ActNode;
using NPS.NWP.ActionNode;

namespace Banyan.Node;

/// <summary>
/// Banyan memory actions expressed through the Ivy Act Node SDK. NWP Action Node
/// support adapts these handlers so the action definitions stay in one place.
/// </summary>
public static class BanyanActNodeActions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<ActActionDescriptor> CreateDescriptors() =>
    [
        new(
            Name: "memory.recall",
            DisplayName: "Recall memory",
            Description: "Search stored Banyan memories by meaning and keywords. Returns ranked hits.",
            Permissions: ["memory.read"],
            InputSchema: RecallInputSchema(),
            OutputSchema: HitsOutputSchema(),
            WorkspaceScoped: true,
            Streaming: false,
            Cancelable: true,
            Risk: "low",
            TimeoutSeconds: 10),
        new(
            Name: "memory.remember",
            DisplayName: "Remember memory",
            Description: "Persist a new memory for future sessions. Returns the assigned memoryId.",
            Permissions: ["memory.write"],
            InputSchema: RememberInputSchema(),
            OutputSchema: ObjectOutputSchema(),
            WorkspaceScoped: true,
            Streaming: false,
            Cancelable: true,
            Risk: "medium",
            TimeoutSeconds: 5),
        new(
            Name: "memory.update",
            DisplayName: "Update memory",
            Description: "Replace the content of an existing memory without changing its ID.",
            Permissions: ["memory.write"],
            InputSchema: MemoryContentInputSchema(),
            OutputSchema: ObjectOutputSchema(),
            WorkspaceScoped: true,
            Streaming: false,
            Cancelable: true,
            Risk: "medium",
            TimeoutSeconds: 5),
        new(
            Name: "memory.forget",
            DisplayName: "Forget memory",
            Description: "Delete a stored memory by ID. The audit trace is preserved.",
            Permissions: ["memory.write"],
            InputSchema: ForgetInputSchema(),
            OutputSchema: ObjectOutputSchema(),
            WorkspaceScoped: true,
            Streaming: false,
            Cancelable: true,
            Risk: "high",
            TimeoutSeconds: 5)
    ];

    public static IReadOnlyList<IActActionHandler> CreateHandlers(IMemoryStore store) =>
        CreateHandlers(store, new SemaphoreSlim(1, 1));

    private static IReadOnlyList<IActActionHandler> CreateHandlers(IMemoryStore store, SemaphoreSlim gate) =>
    [
        new RecallMemoryHandler(store, gate),
        new RememberMemoryHandler(store, gate),
        new UpdateMemoryHandler(store, gate),
        new ForgetMemoryHandler(store, gate)
    ];

    public static IReadOnlyDictionary<string, ActionSpec> CreateNwpActionSpecs() =>
        CreateDescriptors().ToDictionary(
            action => action.Name,
            action => new ActionSpec
            {
                Description = action.Description,
                Async = false,
                Idempotent = action.Name is "memory.recall" or "memory.update" or "memory.forget",
                TimeoutMsDefault = checked((uint)action.TimeoutSeconds * 1000),
                RequiredCapability = action.Permissions?.FirstOrDefault()
            },
            StringComparer.OrdinalIgnoreCase);

    private sealed class RecallMemoryHandler(IMemoryStore store, SemaphoreSlim gate) : IActActionHandler
    {
        public string ActionName => "memory.recall";

        public async Task<JsonNode?> InvokeAsync(ActActionContext context, CancellationToken cancellationToken = default)
        {
            var args = RequireArgs(context, ActionName);
            var query = StringArg(args, "query")
                        ?? throw new InvalidOperationException("memory.recall: 'query' is required.");
            var ns = StringArg(args, "namespace");
            var k = IntArg(args, "k", "top_k") ?? 5;
            var mode = (StringArg(args, "mode") ?? "hybrid").ToLowerInvariant() switch
            {
                "lexical" => SearchMode.Lexical,
                "vector" => SearchMode.Vector,
                _ => SearchMode.Hybrid
            };

            await gate.WaitAsync(cancellationToken);
            try
            {
                var hits = new List<object>();
                await foreach (var hit in store.SearchAsync(new SearchQuery(query, k, ns, mode), cancellationToken))
                {
                    hits.Add(new
                    {
                        memoryId = hit.Memory.Id.ToString(),
                        @namespace = hit.Memory.Namespace,
                        content = hit.Memory.Content,
                        score = hit.Score
                    });
                }

                return JsonSerializer.SerializeToNode(new { count = hits.Count, hits }, JsonOptions);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class RememberMemoryHandler(IMemoryStore store, SemaphoreSlim gate) : IActActionHandler
    {
        public string ActionName => "memory.remember";

        public async Task<JsonNode?> InvokeAsync(ActActionContext context, CancellationToken cancellationToken = default)
        {
            var args = RequireArgs(context, ActionName);
            var content = StringArg(args, "content")
                          ?? throw new InvalidOperationException("memory.remember: 'content' is required.");
            var ns = StringArg(args, "namespace") ?? "default";
            await gate.WaitAsync(cancellationToken);
            try
            {
                var id = await store.WriteAsync(new WriteRequest(content, ns, AgentNid: context.AgentId), cancellationToken);
                return JsonSerializer.SerializeToNode(new { memoryId = id.ToString(), @namespace = ns }, JsonOptions);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class UpdateMemoryHandler(IMemoryStore store, SemaphoreSlim gate) : IActActionHandler
    {
        public string ActionName => "memory.update";

        public async Task<JsonNode?> InvokeAsync(ActActionContext context, CancellationToken cancellationToken = default)
        {
            var args = RequireArgs(context, ActionName);
            var id = RequiredMemoryId(args, ActionName);
            var content = StringArg(args, "content")
                          ?? throw new InvalidOperationException("memory.update: 'content' is required.");

            await gate.WaitAsync(cancellationToken);
            try
            {
                await store.UpdateAsync(new MemoryId(id), new UpdateRequest(content, AgentNid: context.AgentId), cancellationToken);
                return JsonSerializer.SerializeToNode(new { memoryId = id.ToString(), updated = true }, JsonOptions);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private sealed class ForgetMemoryHandler(IMemoryStore store, SemaphoreSlim gate) : IActActionHandler
    {
        public string ActionName => "memory.forget";

        public async Task<JsonNode?> InvokeAsync(ActActionContext context, CancellationToken cancellationToken = default)
        {
            var args = RequireArgs(context, ActionName);
            var id = RequiredMemoryId(args, ActionName);
            var reason = StringArg(args, "reason");

            await gate.WaitAsync(cancellationToken);
            try
            {
                await store.ForgetAsync(new MemoryId(id), reason, cancellationToken);
                return JsonSerializer.SerializeToNode(new { memoryId = id.ToString(), forgotten = true }, JsonOptions);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static JsonObject RequireArgs(ActActionContext context, string actionName) =>
        context.Arguments as JsonObject
        ?? throw new InvalidOperationException($"{actionName}: arguments object is required.");

    private static Guid RequiredMemoryId(JsonObject args, string actionName)
    {
        var value = StringArg(args, "memoryId", "memory_id")
                    ?? throw new InvalidOperationException($"{actionName}: 'memoryId' is required.");
        if (!Guid.TryParse(value, out var id))
            throw new InvalidOperationException($"{actionName}: invalid memoryId '{value}'.");
        return id;
    }

    private static string? StringArg(JsonObject args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetPropertyValue(name, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return null;
    }

    private static int? IntArg(JsonObject args, params string[] names)
    {
        foreach (var name in names)
        {
            if (args.TryGetPropertyValue(name, out var node) &&
                node is JsonValue value &&
                value.TryGetValue<int>(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static JsonObject ObjectOutputSchema() =>
        new()
        {
            ["type"] = "object",
            ["additionalProperties"] = true
        };

    private static JsonObject HitsOutputSchema() =>
        new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["count"] = new JsonObject { ["type"] = "integer" },
                ["hits"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = ObjectOutputSchema()
                }
            }
        };

    private static JsonObject RecallInputSchema() =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("query"),
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string" },
                ["namespace"] = new JsonObject { ["type"] = "string" },
                ["k"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["top_k"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                ["mode"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("hybrid", "lexical", "vector")
                }
            },
            ["additionalProperties"] = true
        };

    private static JsonObject RememberInputSchema() =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("content"),
            ["properties"] = new JsonObject
            {
                ["content"] = new JsonObject { ["type"] = "string" },
                ["namespace"] = new JsonObject { ["type"] = "string" }
            },
            ["additionalProperties"] = true
        };

    private static JsonObject MemoryContentInputSchema() =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("memoryId", "content"),
            ["properties"] = new JsonObject
            {
                ["memoryId"] = new JsonObject { ["type"] = "string" },
                ["memory_id"] = new JsonObject { ["type"] = "string" },
                ["content"] = new JsonObject { ["type"] = "string" }
            },
            ["additionalProperties"] = true
        };

    private static JsonObject ForgetInputSchema() =>
        new()
        {
            ["type"] = "object",
            ["required"] = new JsonArray("memoryId"),
            ["properties"] = new JsonObject
            {
                ["memoryId"] = new JsonObject { ["type"] = "string" },
                ["memory_id"] = new JsonObject { ["type"] = "string" },
                ["reason"] = new JsonObject { ["type"] = "string" }
            },
            ["additionalProperties"] = true
        };
}
