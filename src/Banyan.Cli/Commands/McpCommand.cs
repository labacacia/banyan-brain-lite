// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Banyan.Cli.Commands;

internal static class McpCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (CommandContext.HasFlag(args, "--help") || CommandContext.HasFlag(args, "-h"))
        {
            Console.Error.WriteLine("""
                banyan mcp — run Banyan as a Model Context Protocol (MCP) stdio server

                Usage: banyan mcp [options]

                  --db PATH          Path to memory.db         (default: ~/.banyan/memory.db)
                  --namespace NS     Default write namespace    (default: default)
                  --sqlite-vec PATH  sqlite-vec extension path  (auto-discover if omitted)

                Claude Desktop  (~/.../claude_desktop_config.json):
                  { "mcpServers": { "banyan": { "command": "banyan", "args": ["mcp"] } } }

                Claude Code:
                  claude mcp add banyan -- banyan mcp
                """);
            return 0;
        }

        var dbPath    = CommandContext.GetOption(args, "--db") ?? "~/.banyan/memory.db";
        var defaultNs = CommandContext.GetOption(args, "--namespace") ?? "default";
        var vecLib    = CommandContext.GetOption(args, "--sqlite-vec");

        var expandedDb = CommandContext.ExpandHome(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(expandedDb)!);

        // Embedder logs go to stderr so they don't corrupt the MCP stdio channel.
        IEmbedder embedder = EmbedderFactory.Create(Console.Error);
        var store = await SqliteMemoryStore.OpenAsync(
            $"Data Source={expandedDb}", embedder, vecLib);

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

        // Suppress all console logging — stdout is the MCP JSON-RPC channel.
        builder.Logging.ClearProviders();

        builder.Services
            .AddSingleton<IMemoryStore>(store)
            .AddSingleton(new McpDefaults(defaultNs))
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<BanyanMemoryTools>();

        await using (store)
            await builder.Build().RunAsync();

        return 0;
    }
}

internal sealed record McpDefaults(string Namespace);

[McpServerToolType]
internal sealed class BanyanMemoryTools(IMemoryStore store, McpDefaults defaults)
{
    [McpServerTool(Name = "recall")]
    [Description(
        "Search stored memories by meaning and keywords. " +
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
        "Persist a new memory for future sessions. " +
        "Use when the user says 'remember X', corrects a previous answer, or states a hard preference. " +
        "Returns the memory ID.")]
    public async Task<string> RememberAsync(
        [Description("Content to store — a distilled fact or preference, not a raw conversation log")] string content,
        [Description("Namespace for this memory (e.g. 'user-alice'). Uses the server default if omitted.")] string? @namespace = null)
    {
        var id = await store.WriteAsync(new WriteRequest(content, @namespace ?? defaults.Namespace));
        return $"Stored. id={id}";
    }

    [McpServerTool(Name = "update")]
    [Description("Replace the content of an existing memory without changing its ID or namespace.")]
    public async Task<string> UpdateAsync(
        [Description("Memory ID shown in 'recall' results or returned by 'remember'")] string memoryId,
        [Description("New content to replace the existing memory")] string content)
    {
        if (!Guid.TryParse(memoryId, out var guid))
            return $"Invalid memory ID: '{memoryId}'. Must be a UUID.";

        await store.UpdateAsync(new MemoryId(guid), new UpdateRequest(content));
        return $"Updated. id={memoryId}";
    }

    [McpServerTool(Name = "forget")]
    [Description(
        "Delete a stored memory by ID. " +
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
