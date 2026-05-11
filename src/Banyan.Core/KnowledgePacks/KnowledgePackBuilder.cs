using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banyan.Core.KnowledgePacks;

public sealed class KnowledgePackBuildOptions
{
    public required string PackId { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string PackType { get; init; } = "knowledge";
    public IReadOnlyList<string> ContentTypes { get; init; } = ["document"];
    public IReadOnlyList<string> TargetScopes { get; init; } = ["user", "agent"];
}

public sealed record KnowledgePackBuildResult(
    KnowledgePackManifest Manifest,
    IReadOnlyList<KnowledgePackSourceRecord> Sources,
    IReadOnlyList<KnowledgePackMemoryRecord> Memories,
    IReadOnlyList<KnowledgePackArchiveEntry> Entries);

public static class KnowledgePackBuilder
{
    private static readonly string[] SupportedExtensions = [".md", ".txt", ".json"];

    public static async Task<KnowledgePackBuildResult> BuildFromPathAsync(
        string path,
        KnowledgePackBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Source path does not exist: {path}");
        }

        var files = ResolveFiles(fullPath);
        if (files.Count == 0)
        {
            throw new InvalidOperationException("No supported source files found. Supported extensions: .md, .txt, .json.");
        }

        var baseDir = File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath)!
            : fullPath;

        var sources = new List<KnowledgePackSourceRecord>();
        var memories = new List<KnowledgePackMemoryRecord>();
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var checksum = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var relativePath = ToPosixRelativePath(baseDir, file);
            var sourceId = StableId("source", relativePath);
            var content = Encoding.UTF8.GetString(bytes);
            var mediaType = ResolveMediaType(file);

            sources.Add(new KnowledgePackSourceRecord(
                sourceId,
                relativePath,
                Path.GetFileName(file),
                mediaType,
                checksum,
                bytes.Length));

            checksums[$"sources/{sourceId}.json"] = checksum;
            memories.Add(new KnowledgePackMemoryRecord(
                StableId("memory", relativePath),
                sourceId,
                "document",
                content,
                relativePath,
                Confidence: null));
        }

        var manifest = new KnowledgePackManifest
        {
            PackId = options.PackId,
            Name = options.Name,
            Version = options.Version,
            Description = options.Description,
            Publisher = options.Publisher,
            CreatedAt = DateTimeOffset.UtcNow,
            PackType = options.PackType,
            ContentTypes = options.ContentTypes,
            TargetScopes = options.TargetScopes,
            Permissions = new KnowledgePackPermissions { AllowRecall = true },
            Indexes = new KnowledgePackIndexes { Keyword = true },
            Checksums = checksums
        };

        KnowledgePackManifestValidator.Validate(manifest).ThrowIfInvalid();

        var entries = new List<KnowledgePackArchiveEntry>
        {
            JsonEntry("sources/sources.jsonl", sources),
            JsonEntry("memories/records.jsonl", memories)
        };

        return new KnowledgePackBuildResult(manifest, sources, memories, entries);
    }

    public static IReadOnlyList<string> GetSupportedExtensions() => SupportedExtensions;

    private static List<string> ResolveFiles(string fullPath)
    {
        if (File.Exists(fullPath))
        {
            return IsSupported(fullPath) ? [fullPath] : [];
        }

        return Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
            .Where(IsSupported)
            .OrderBy(static p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string ResolveMediaType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };

    private static KnowledgePackArchiveEntry JsonEntry<T>(string path, IReadOnlyList<T> records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record, KnowledgePackArchive.JsonOptions));
        return new KnowledgePackArchiveEntry(path, Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
    }

    private static string StableId(string prefix, string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"{prefix}_{Convert.ToHexString(bytes)[..16].ToLowerInvariant()}";
    }

    private static string ToPosixRelativePath(string baseDir, string file)
    {
        var relative = Path.GetRelativePath(baseDir, file);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}

public sealed record KnowledgePackSourceRecord(
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("checksum")] string Checksum,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

public sealed record KnowledgePackMemoryRecord(
    [property: JsonPropertyName("record_id")] string RecordId,
    [property: JsonPropertyName("source_id")] string SourceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("confidence")] double? Confidence);
