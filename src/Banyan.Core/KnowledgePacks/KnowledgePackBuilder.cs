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
    IReadOnlyList<KnowledgePackArchiveEntry> Entries,
    IReadOnlyList<KnowledgePackReviewEntry> ReviewQueue);

public static class KnowledgePackBuilder
{
    private static readonly string[] SupportedExtensions = [".md", ".txt", ".json", ".csv"];

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

        var sources   = new List<KnowledgePackSourceRecord>();
        var memories  = new List<KnowledgePackMemoryRecord>();
        var review    = new List<KnowledgePackReviewEntry>();
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
            var memoryId = StableId("memory", relativePath);
            memories.Add(new KnowledgePackMemoryRecord(
                memoryId,
                sourceId,
                "document",
                content,
                relativePath,
                Confidence: null));
            review.Add(new KnowledgePackReviewEntry(memoryId, sourceId, relativePath, "accept"));
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
            JsonEntry("memories/records.jsonl", memories),
            JsonEntry("review/queue.jsonl", review),
        };

        return new KnowledgePackBuildResult(manifest, sources, memories, entries, review);
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
            ".md"  => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".csv" => "text/csv",
            _ => "application/octet-stream"
        };

    // JSONL must be one JSON object per line — never use WriteIndented here.
    private static readonly JsonSerializerOptions JsonlOptions = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static KnowledgePackArchiveEntry JsonEntry<T>(string path, IReadOnlyList<T> records)
    {
        var lines = records.Select(record => JsonSerializer.Serialize(record, JsonlOptions));
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

/// <summary>
/// One entry in <c>review/queue.jsonl</c>. Operators can set the
/// <see cref="Decision"/> field to <c>accept</c>, <c>reject</c>, or
/// <c>edit</c> before mounting the pack.
/// </summary>
public sealed record KnowledgePackReviewEntry(
    [property: JsonPropertyName("record_id")]   string RecordId,
    [property: JsonPropertyName("source_id")]   string SourceId,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("decision")]    string Decision);
