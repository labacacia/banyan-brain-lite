// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Banyan.Core;
using Banyan.Lite;
using ModelContextProtocol.Server;

namespace Banyan.Mcp;

public sealed record McpDefaults(string Namespace);

public interface IBanyanMcpAgentContext
{
    string? CurrentAgentNid { get; }
}

public sealed class NullBanyanMcpAgentContext : IBanyanMcpAgentContext
{
    public string? CurrentAgentNid => null;
}

[McpServerToolType]
public sealed class BanyanMemoryTools(
    IMemoryStore store,
    McpDefaults defaults,
    IBanyanMcpAgentContext agentContext)
{
    [McpServerTool(Name = "recall")]
    [Description(
        "Search stored Banyan memories by meaning and keywords. " +
        "Call this before generating a response to surface relevant context. " +
        "Returns ranked snippets with score and ID.")]
    public async Task<string> RecallAsync(
        [Description("Natural language search query")] string query,
        [Description(
            "Namespace to scope the search (e.g. 'user-alice', 'project-banyan'). " +
            "Omit to search across all namespaces.")] string? @namespace = null,
        [Description("Maximum number of results to return")] int k = 5,
        [Description("Search mode: 'hybrid' (default, best quality), 'lexical' (keyword), or 'vector' (semantic)")] string mode = "hybrid")
    {
        var sm = mode.ToLowerInvariant() switch
        {
            "lexical" => SearchMode.Lexical,
            "vector"  => SearchMode.Vector,
            _         => SearchMode.Hybrid,
        };

        var hits = new List<SearchHit>();
        await foreach (var h in store.SearchAsync(new SearchQuery(query, k, @namespace, sm)))
            hits.Add(h);

        if (hits.Count == 0)
            return "No matching memories found.";

        return string.Join("\n\n", hits.Select((h, i) =>
            $"[{i + 1}] score={h.Score:F3}  id={h.Memory.Id}  ns={h.Memory.Namespace}\n{h.Memory.Content}"));
    }

    [McpServerTool(Name = "remember")]
    [Description(
        "Persist a new Banyan memory for future sessions. " +
        "Use when the user says 'remember X', corrects a previous answer, or states a hard preference. " +
        "Returns the memory ID.")]
    public async Task<string> RememberAsync(
        [Description("Content to store - a distilled fact or preference, not a raw conversation log")] string content,
        [Description("Namespace for this memory (e.g. 'user-alice'). Uses the server default if omitted.")] string? @namespace = null)
    {
        var id = await store.WriteAsync(new WriteRequest(
            Content: content,
            Namespace: @namespace ?? defaults.Namespace,
            AgentNid: agentContext.CurrentAgentNid));
        return $"Stored. id={id}";
    }

    [McpServerTool(Name = "update")]
    [Description("Replace the content of an existing Banyan memory without changing its ID or namespace.")]
    public async Task<string> UpdateAsync(
        [Description("Memory ID shown in 'recall' results or returned by 'remember'")] string memoryId,
        [Description("New content to replace the existing memory")] string content)
    {
        if (!Guid.TryParse(memoryId, out var guid))
            return $"Invalid memory ID: '{memoryId}'. Must be a UUID.";

        await store.UpdateAsync(new MemoryId(guid), new UpdateRequest(content, AgentNid: agentContext.CurrentAgentNid));
        return $"Updated. id={memoryId}";
    }

    [McpServerTool(Name = "forget")]
    [Description(
        "Delete a stored Banyan memory by ID. " +
        "The content is removed from search; the audit trace is preserved. " +
        "Use when the user asks to forget something or when a memory is no longer accurate.")]
    public async Task<string> ForgetAsync(
        [Description("Memory ID to delete")] string memoryId,
        [Description("Optional reason for deletion, recorded in the audit log")] string? reason = null)
    {
        if (!Guid.TryParse(memoryId, out var guid))
            return $"Invalid memory ID: '{memoryId}'. Must be a UUID.";

        await store.ForgetAsync(new MemoryId(guid), reason);
        return $"Forgotten. id={memoryId}";
    }
}
