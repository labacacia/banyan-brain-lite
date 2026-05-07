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

            var outPath = CommandContext.ExpandHome(output);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await using var stream = File.Create(outPath);
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

    private static void PrintBuildSummary(KnowledgePackBuildResult result, string? outputPath)
    {
        Console.WriteLine($"pack_id:       {result.Manifest.PackId}");
        Console.WriteLine($"name:          {result.Manifest.Name}");
        Console.WriteLine($"version:       {result.Manifest.Version}");
        Console.WriteLine($"sources:       {result.Sources.Count}");
        Console.WriteLine($"memories:      {result.Memories.Count}");
        if (outputPath is not null)
        {
            Console.WriteLine($"output:        {outputPath}");
        }
    }

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
              inspect PATH Inspect a .banyanpack manifest
            """);
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"banyan pack: unknown subcommand '{sub}'. Run `banyan pack --help`.");
        return 64;
    }
}
