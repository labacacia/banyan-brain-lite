using System.Text.Json;
using Banyan.Core.KnowledgePacks;

namespace Banyan.Cli.Commands;

internal static class PackCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { Help(); return 64; }
        var sub = args[0];
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "build" => await BuildAsync(rest),
            "inspect" => await InspectAsync(rest),
            "mount" => await MountAsync(rest),
            "list" => await ListAsync(rest),
            "unmount" => await UnmountAsync(rest),
            "--help" or "-h" or "help" => Help(),
            _ => Unknown(sub),
        };
    }

    private static async Task<int> BuildAsync(string[] args)
    {
        var source = FirstValue(args);
        if (source is null)
        {
            Console.Error.WriteLine("banyan pack build: source path is required.");
            return 64;
        }

        var output = CommandContext.GetOption(args, "--out");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("banyan pack build: --out PATH is required.");
            return 64;
        }

        var packId = CommandContext.GetOption(args, "--pack-id");
        var name = CommandContext.GetOption(args, "--name");
        var version = CommandContext.GetOption(args, "--version") ?? "0.1.0";
        if (string.IsNullOrWhiteSpace(packId))
        {
            Console.Error.WriteLine("banyan pack build: --pack-id ID is required.");
            return 64;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = packId;
        }

        try
        {
            var result = await KnowledgePackBuilder.BuildFromPathAsync(
                CommandContext.ExpandHome(source),
                new KnowledgePackBuildOptions
                {
                    PackId = packId,
                    Name = name,
                    Version = version,
                    Description = CommandContext.GetOption(args, "--description"),
                    Publisher = CommandContext.GetOption(args, "--publisher"),
                    PackType = CommandContext.GetOption(args, "--pack-type") ?? "knowledge",
                    ContentTypes = SplitCsv(CommandContext.GetOption(args, "--content-types")) ?? ["document"],
                    TargetScopes = SplitCsv(CommandContext.GetOption(args, "--target-scopes")) ?? ["user", "agent"]
                });

            if (CommandContext.HasFlag(args, "--dry-run"))
            {
                PrintBuildSummary(result, outputPath: null);
                return 0;
            }

            var outPath    = CommandContext.ExpandHome(output);
            var passphrase = CommandContext.GetOption(args, "--passphrase");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await using var stream = File.Create(outPath);
            if (!string.IsNullOrEmpty(passphrase))
                await KnowledgePackArchive.WriteEncryptedAsync(stream, result.Manifest, result.Entries, passphrase);
            else
                await KnowledgePackArchive.WriteAsync(stream, result.Manifest, result.Entries);

            PrintBuildSummary(result, outPath);
            return 0;
        }
        catch (Exception ex) when (ex is KnowledgePackValidationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or InvalidOperationException)
        {
            Console.Error.WriteLine($"banyan pack build: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> InspectAsync(string[] args)
    {
        var pack = FirstValue(args);
        if (pack is null)
        {
            Console.Error.WriteLine("banyan pack inspect: pack path is required.");
            return 64;
        }

        try
        {
            await using var stream = File.OpenRead(CommandContext.ExpandHome(pack));
            var manifest = await KnowledgePackArchive.ReadManifestAsync(stream);

            Console.WriteLine($"pack_id:       {manifest.PackId}");
            Console.WriteLine($"name:          {manifest.Name}");
            Console.WriteLine($"version:       {manifest.Version}");
            Console.WriteLine($"schema:        {manifest.SchemaVersion}");
            Console.WriteLine($"pack_type:     {manifest.PackType}");
            Console.WriteLine($"publisher:     {manifest.Publisher ?? "-"}");
            Console.WriteLine($"created_at:    {manifest.CreatedAt:O}");
            Console.WriteLine($"content_types: {string.Join(", ", manifest.ContentTypes)}");
            Console.WriteLine($"target_scopes: {string.Join(", ", manifest.TargetScopes)}");
            Console.WriteLine($"indexes:       keyword={manifest.Indexes.Keyword}, vector={manifest.Indexes.Vector}, graph={manifest.Indexes.Graph}");
            Console.WriteLine($"permissions:   recall={manifest.Permissions.AllowRecall}, export={manifest.Permissions.AllowExport}, finetune={manifest.Permissions.AllowFinetune}");
            Console.WriteLine($"checksums:     {manifest.Checksums.Count}");
            Console.WriteLine($"extensions:    {manifest.Extensions.Count}");
            if (manifest.Encryption is { } enc)
            {
                Console.WriteLine($"encrypted:     yes");
                Console.WriteLine($"  algorithm:   {enc.Algorithm}");
                Console.WriteLine($"  kdf:         {enc.Kdf}");
                Console.WriteLine($"  iterations:  {enc.Iterations}");
            }
            else
            {
                Console.WriteLine($"encrypted:     no");
            }
            return 0;
        }
        catch (Exception ex) when (ex is KnowledgePackValidationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            Console.Error.WriteLine($"banyan pack inspect: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> MountAsync(string[] args)
    {
        var pack = FirstValue(args);
        if (pack is null)
        {
            Console.Error.WriteLine("banyan pack mount: pack path is required.");
            return 64;
        }

        var @namespace = CommandContext.GetOption(args, "--namespace");
        if (string.IsNullOrWhiteSpace(@namespace))
        {
            Console.Error.WriteLine("banyan pack mount: --namespace NS is required.");
            return 64;
        }

        try
        {
            var registry = OpenRegistry(args);
            var result = await registry.MountAsync(
                CommandContext.ExpandHome(pack),
                @namespace,
                mountedBy: CommandContext.GetOption(args, "--mounted-by"),
                passphrase: CommandContext.GetOption(args, "--passphrase"));

            Console.WriteLine(result.Created ? "mounted:      created" : "mounted:      already exists");
            PrintMountRecord(result.Record);
            return 0;
        }
        catch (KnowledgePackWrongPassphraseException ex)
        {
            Console.Error.WriteLine($"banyan pack mount: wrong passphrase — {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is KnowledgePackValidationException
                                   or IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or ArgumentException)
        {
            Console.Error.WriteLine($"banyan pack mount: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> ListAsync(string[] args)
    {
        try
        {
            var records = await OpenRegistry(args).ListAsync(CommandContext.GetOption(args, "--namespace"));
            if (records.Count == 0)
            {
                Console.WriteLine("No mounted knowledge packs.");
                return 0;
            }

            foreach (var record in records)
            {
                PrintMountRecord(record);
                Console.WriteLine();
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"banyan pack list: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> UnmountAsync(string[] args)
    {
        var packId = FirstValue(args);
        if (packId is null)
        {
            Console.Error.WriteLine("banyan pack unmount: pack id is required.");
            return 64;
        }

        var @namespace = CommandContext.GetOption(args, "--namespace");
        if (string.IsNullOrWhiteSpace(@namespace))
        {
            Console.Error.WriteLine("banyan pack unmount: --namespace NS is required.");
            return 64;
        }

        try
        {
            var removed = await OpenRegistry(args).UnmountAsync(
                packId,
                @namespace,
                CommandContext.GetOption(args, "--version"));

            if (!removed)
            {
                Console.Error.WriteLine("banyan pack unmount: no matching mount found.");
                return 2;
            }

            Console.WriteLine($"unmounted:    {packId}");
            Console.WriteLine($"namespace:    {@namespace}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.Error.WriteLine($"banyan pack unmount: {ex.Message}");
            return 2;
        }
    }

    private static void PrintBuildSummary(KnowledgePackBuildResult result, string? outputPath)
    {
        Console.WriteLine($"pack_id:       {result.Manifest.PackId}");
        Console.WriteLine($"name:          {result.Manifest.Name}");
        Console.WriteLine($"version:       {result.Manifest.Version}");
        Console.WriteLine($"sources:       {result.Sources.Count}");
        Console.WriteLine($"memories:      {result.Memories.Count}");
        Console.WriteLine($"review:        {result.ReviewQueue.Count}");
        if (outputPath is not null)
        {
            Console.WriteLine($"output:        {outputPath}");
        }
    }

    private static void PrintMountRecord(KnowledgePackMountRecord record)
    {
        Console.WriteLine($"namespace:    {record.Namespace}");
        Console.WriteLine($"pack_id:      {record.PackId}");
        Console.WriteLine($"version:      {record.PackVersion}");
        Console.WriteLine($"name:         {record.PackName}");
        Console.WriteLine($"pack_type:    {record.PackType}");
        Console.WriteLine($"enabled:      {record.Enabled}");
        Console.WriteLine($"checksum:     {record.PackChecksum}");
        Console.WriteLine($"pack_path:    {record.PackPath}");
        Console.WriteLine($"mounted_at:   {record.MountedAt:O}");
    }

    private static FileKnowledgePackMountRegistry OpenRegistry(string[] args)
        => new(CommandContext.ExpandHome(
            CommandContext.GetOption(args, "--registry")
            ?? "~/.banyan/knowledge-packs/mounts.json"));

    private static string? FirstValue(string[] args)
        => args.FirstOrDefault(static arg => !arg.StartsWith("-", StringComparison.Ordinal));

    private static IReadOnlyList<string>? SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split([","], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int Help()
    {
        Console.WriteLine("""
            banyan pack <subcommand>
              build PATH   Build a portable .banyanpack from .md, .txt, and .json files
                             --out PATH             output .banyanpack
                             --pack-id ID           e.g. com.company.products
                             --name NAME            default: pack id
                             --version VERSION      default: 0.1.0
                             --description TEXT
                             --publisher ID         e.g. nid:company-a
                             --pack-type TYPE       default: knowledge
                             --content-types CSV    default: document
                             --target-scopes CSV    default: user,agent
                             --dry-run              print summary without writing
                             --passphrase PASS      encrypt with AES-256-GCM + PBKDF2-SHA256
              inspect PATH Inspect a .banyanpack manifest
              mount PATH   Mount a .banyanpack into a namespace
                             --namespace NS         required
                             --registry PATH        default: ~/.banyan/knowledge-packs/mounts.json
                             --mounted-by ID
                             --passphrase PASS      required for encrypted packs
              list         List mounted packs
                             --namespace NS
                             --registry PATH
              unmount ID   Unmount a pack id from a namespace
                             --namespace NS         required
                             --version VERSION      optional
                             --registry PATH
            """);
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"banyan pack: unknown subcommand '{sub}'. Run `banyan pack --help`.");
        return 64;
    }
}
