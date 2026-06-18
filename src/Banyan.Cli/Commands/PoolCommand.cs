// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Lite;

namespace Banyan.Cli.Commands;

internal static class PoolCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || CommandContext.HasFlag(args, "--help") || CommandContext.HasFlag(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        var dbPath = CommandContext.ExpandHome(
            CommandContext.GetOption(args, "--db")
            ?? Environment.GetEnvironmentVariable("BANYAN_MEMORY_DB")
            ?? "~/.banyan/memory.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var repo = await SqliteMemoryPoolRepository.OpenAsync($"Data Source={dbPath}");

        return args[0] switch
        {
            "create" => await CreateAsync(repo, args[1..]),
            "list" => await ListAsync(repo),
            "add-member" => await AddMemberAsync(repo, args[1..]),
            "remove-member" => await RemoveMemberAsync(repo, args[1..]),
            _ => Unknown(args[0]),
        };
    }

    private static async Task<int> CreateAsync(IMemoryPoolRepository repo, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("pool create requires a name.");
            return 64;
        }

        var scope = CommandContext.GetOption(args, "--scope")
            ?? CommandContext.GetOption(args, "--level")
            ?? "personal";
        var owner = CommandContext.GetOption(args, "--owner")
            ?? Environment.GetEnvironmentVariable("BANYAN_AGENT_ID")
            ?? Environment.UserName;

        var pool = await repo.CreateAsync(args[0], scope, owner);
        Console.WriteLine($"id:      {pool.Id}");
        Console.WriteLine($"name:    {pool.Name}");
        Console.WriteLine($"scope:   {pool.Scope}");
        Console.WriteLine($"owner:   {pool.OwnerId}");
        Console.WriteLine($"created: {pool.CreatedAt:O}");
        return 0;
    }

    private static async Task<int> ListAsync(IMemoryPoolRepository repo)
    {
        var pools = await repo.ListAsync();
        foreach (var pool in pools)
            Console.WriteLine($"{pool.Id}\t{pool.Scope}\t{pool.OwnerId}\t{pool.Name}");
        return 0;
    }

    private static async Task<int> AddMemberAsync(IMemoryPoolRepository repo, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("pool add-member requires <pool-id-or-name> <member-id>.");
            return 64;
        }

        var pool = await ResolvePoolAsync(repo, args[0]);
        if (pool is null) return 66;

        var type = CommandContext.GetOption(args, "--type") ?? "agent";
        await repo.AddMemberAsync(pool.Id, args[1], type);
        await repo.BindAgentAsync(args[1], pool.Id);
        Console.WriteLine($"added: {args[1]} -> {pool.Id}");
        return 0;
    }

    private static async Task<int> RemoveMemberAsync(IMemoryPoolRepository repo, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("pool remove-member requires <pool-id-or-name> <member-id>.");
            return 64;
        }

        var pool = await ResolvePoolAsync(repo, args[0]);
        if (pool is null) return 66;

        await repo.RemoveMemberAsync(pool.Id, args[1]);
        Console.WriteLine($"removed: {args[1]} -> {pool.Id}");
        return 0;
    }

    private static async Task<MemoryPool?> ResolvePoolAsync(IMemoryPoolRepository repo, string poolIdOrName)
    {
        var pool = await repo.GetAsync(poolIdOrName);
        if (pool is not null) return pool;

        Console.Error.WriteLine($"pool not found: {poolIdOrName}");
        return null;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"banyan pool: unknown subcommand '{sub}'.");
        return 64;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: banyan pool <command> [options]

              pool create <name> [--scope personal|workspace|agent] [--owner ID] [--db PATH]
              pool list [--db PATH]
              pool add-member <pool-id-or-name> <member-id> [--type agent|user|session] [--db PATH]
              pool remove-member <pool-id-or-name> <member-id> [--db PATH]
            """);
    }
}
