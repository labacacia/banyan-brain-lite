// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Core.KnowledgePacks;
using Banyan.Embedders;
using Banyan.Lite;
using Banyan.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
            .AddSingleton<IMemoryStore>(new KnowledgePackRecallStore(
                store,
                new FileKnowledgePackMountRegistry(FileKnowledgePackMountRegistry.DefaultPath)))
            .AddSingleton(new McpDefaults(defaultNs))
            .AddSingleton<IBanyanMcpAgentContext, NullBanyanMcpAgentContext>()
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<BanyanMemoryTools>();

        await using (store)
            await builder.Build().RunAsync();

        return 0;
    }
}
