using System.IO.Compression;
using System.Text.Json;

namespace Banyan.Core.KnowledgePacks;

public static class KnowledgePackArchive
{
    public const string ManifestPath = "manifest.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteAsync(
        Stream output,
        KnowledgePackManifest manifest,
        IEnumerable<KnowledgePackArchiveEntry>? entries = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(manifest);

        KnowledgePackManifestValidator.Validate(manifest).ThrowIfInvalid();

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonEntryAsync(archive, ManifestPath, manifest, cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries ?? [])
        {
            ValidateEntryPath(entry.Path);
            if (string.Equals(entry.Path, ManifestPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Archive entries must not override manifest.json.", nameof(entries));
            }

            var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
            await using var stream = zipEntry.Open();
            await stream.WriteAsync(entry.Content, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<KnowledgePackManifest> ReadManifestAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        return await ReadManifestAsync(archive, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<KnowledgePackManifest> ReadManifestAsync(
        ZipArchive archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var entry = archive.GetEntry(ManifestPath)
            ?? throw new InvalidDataException("Knowledge pack archive is missing manifest.json.");

        await using var stream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<KnowledgePackManifest>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        KnowledgePackManifestValidator.Validate(manifest).ThrowIfInvalid();
        return manifest!;
    }

    public static async Task<KnowledgePackValidationResult> ValidateAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await ReadManifestAsync(input, cancellationToken).ConfigureAwait(false);
            return new KnowledgePackValidationResult([]);
        }
        catch (KnowledgePackValidationException ex)
        {
            return new KnowledgePackValidationResult(ex.Errors);
        }
        catch (InvalidDataException ex)
        {
            return new KnowledgePackValidationResult([ex.Message]);
        }
        catch (JsonException ex)
        {
            return new KnowledgePackValidationResult([ex.Message]);
        }
    }

    private static async Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Archive entry path must not be blank.", nameof(path));
        }

        if (Path.IsPathRooted(path)
            || path.Contains('\\', StringComparison.Ordinal)
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Archive entry path must be a relative POSIX path without traversal.", nameof(path));
        }
    }
}

public sealed record KnowledgePackArchiveEntry(string Path, ReadOnlyMemory<byte> Content);
