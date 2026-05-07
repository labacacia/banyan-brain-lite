using System.Text;
using System.Text.Json;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public sealed class KnowledgePackTests
{
    [Fact]
    public void ManifestValidator_AcceptsMinimalValidManifest()
    {
        var result = KnowledgePackManifestValidator.Validate(ValidManifest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ManifestValidator_RejectsMissingRequiredValues()
    {
        var manifest = ValidManifest() with
        {
            PackId = "Invalid Pack",
            Name = "",
            ContentTypes = [],
            ValidFrom = DateTimeOffset.Parse("2026-05-07T00:00:00Z"),
            ValidUntil = DateTimeOffset.Parse("2026-05-06T00:00:00Z")
        };

        var result = KnowledgePackManifestValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains("pack_id must use lowercase letters, digits, dots, underscores, or hyphens", result.Errors);
        Assert.Contains("name is required", result.Errors);
        Assert.Contains("content_types must contain at least one value", result.Errors);
        Assert.Contains("valid_until must be later than valid_from", result.Errors);
    }

    [Fact]
    public async Task Archive_RoundTripsManifestAndEntries()
    {
        var manifest = ValidManifest();
        await using var stream = new MemoryStream();

        await KnowledgePackArchive.WriteAsync(
            stream,
            manifest,
            [
                new KnowledgePackArchiveEntry("memories/records.jsonl", Encoding.UTF8.GetBytes("{}\n")),
                new KnowledgePackArchiveEntry("sources/source-1.json", Encoding.UTF8.GetBytes("{}"))
            ]);

        stream.Position = 0;
        var read = await KnowledgePackArchive.ReadManifestAsync(stream);

        Assert.Equal(manifest.PackId, read.PackId);
        Assert.Equal(manifest.Version, read.Version);
        Assert.Equal("knowledge", read.PackType);
    }

    [Fact]
    public async Task Archive_RejectsEntryPathTraversal()
    {
        await using var stream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            KnowledgePackArchive.WriteAsync(
                stream,
                ValidManifest(),
                [new KnowledgePackArchiveEntry("../outside.json", Encoding.UTF8.GetBytes("{}"))]));

        Assert.Contains("relative POSIX path", ex.Message);
    }

    [Fact]
    public async Task Archive_ValidateAsync_ReturnsErrorsForMissingManifest()
    {
        await using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("memories/records.jsonl");
        }

        stream.Position = 0;
        var result = await KnowledgePackArchive.ValidateAsync(stream);

        Assert.False(result.IsValid);
        Assert.Contains("Knowledge pack archive is missing manifest.json.", result.Errors);
    }

    [Fact]
    public void Manifest_UsesSnakeCaseJsonContract()
    {
        var json = JsonSerializer.Serialize(ValidManifest(), KnowledgePackArchive.JsonOptions);

        Assert.Contains("\"schema_version\"", json);
        Assert.Contains("\"pack_id\"", json);
        Assert.Contains("\"target_scopes\"", json);
        Assert.DoesNotContain("PackId", json);
    }

    private static KnowledgePackManifest ValidManifest() => new()
    {
        PackId = "com.company-a.products",
        Name = "Company A Product Knowledge",
        Version = "2026.05",
        Description = "Product facts and support policy.",
        Publisher = "nid:company-a",
        CreatedAt = DateTimeOffset.Parse("2026-05-07T00:00:00Z"),
        PackType = "knowledge",
        ContentTypes = ["product", "faq", "policy"],
        TargetScopes = ["user", "agent"],
        Permissions = new KnowledgePackPermissions { AllowRecall = true },
        Indexes = new KnowledgePackIndexes { Keyword = true, Vector = true },
        Checksums = new Dictionary<string, string>
        {
            ["sources/source-1.json"] = "sha256:abc"
        }
    };
}
