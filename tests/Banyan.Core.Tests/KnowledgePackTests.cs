using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
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

    [Fact]
    public async Task Builder_BuildsPackEntriesFromSupportedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "banyan-pack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "overview.md"), "# Product\nA useful product.");
            await File.WriteAllTextAsync(Path.Combine(root, "pricing.json"), "{\"tier\":\"free\"}");
            await File.WriteAllTextAsync(Path.Combine(root, "ignored.bin"), "ignored");

            var result = await KnowledgePackBuilder.BuildFromPathAsync(
                root,
                new KnowledgePackBuildOptions
                {
                    PackId = "com.company-a.products",
                    Name = "Company A Product Knowledge",
                    Version = "2026.05"
                });

            Assert.Equal(2, result.Sources.Count);
            Assert.Equal(2, result.Memories.Count);
            Assert.Equal(2, result.Entries.Count);
            Assert.Contains(result.Sources, static s => s.Path == "overview.md" && s.MediaType == "text/markdown");
            Assert.Contains(result.Sources, static s => s.Path == "pricing.json" && s.MediaType == "application/json");
            Assert.Contains(result.Entries, static e => e.Path == "sources/sources.jsonl");
            Assert.Contains(result.Entries, static e => e.Path == "memories/records.jsonl");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MountRegistry_MountListAndUnmountRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "banyan-mount-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packPath = await CreatePackAsync(root);
            var registryPath = Path.Combine(root, "mounts.json");
            var registry = new FileKnowledgePackMountRegistry(registryPath);

            var first = await registry.MountAsync(packPath, "user:alice", mountedBy: "test");
            var second = await registry.MountAsync(packPath, "user:alice", mountedBy: "test");
            var records = await registry.ListAsync("user:alice");

            Assert.True(first.Created);
            Assert.False(second.Created);
            var record = Assert.Single(records);
            Assert.Equal("user:alice", record.Namespace);
            Assert.Equal("com.company-a.products", record.PackId);
            Assert.Equal("2026.05", record.PackVersion);
            Assert.True(record.Enabled);
            Assert.StartsWith("sha256:", record.PackChecksum, StringComparison.Ordinal);

            Assert.True(await registry.UnmountAsync("com.company-a.products", "user:alice"));
            Assert.Empty(await registry.ListAsync("user:alice"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecallStore_ReturnsMountedPackRecordsWithPackMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "banyan-recall-pack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packPath = await CreatePackAsync(root);
            var registry = new FileKnowledgePackMountRegistry(Path.Combine(root, "mounts.json"));
            await registry.MountAsync(packPath, "user:alice", mountedBy: "test");
            var store = new KnowledgePackRecallStore(new StubMemoryStore(), registry);

            var hits = await CollectAsync(store.SearchAsync(new SearchQuery("useful product", 5, "user:alice", SearchMode.Lexical)));

            var hit = Assert.Single(hits);
            Assert.Equal("user:alice", hit.Memory.Namespace);
            Assert.Contains("useful product", hit.Memory.Content, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("knowledge_pack", hit.Memory.Metadata!.RootElement.GetProperty("source").GetString());
            Assert.Equal("com.company-a.products", hit.Memory.Metadata.RootElement.GetProperty("pack_id").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecallStore_MergesNativeAndMountedPackRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "banyan-recall-merge-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packPath = await CreatePackAsync(root);
            var registry = new FileKnowledgePackMountRegistry(Path.Combine(root, "mounts.json"));
            await registry.MountAsync(packPath, "user:alice", mountedBy: "test");
            var nativeMemory = new Memory(
                MemoryId.New(),
                EventId.New(),
                "user:alice",
                "Native product note",
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var store = new KnowledgePackRecallStore(
                new StubMemoryStore([new SearchHit(nativeMemory, 1.2, null, 1)]),
                registry);

            var hits = await CollectAsync(store.SearchAsync(new SearchQuery("product", 5, "user:alice", SearchMode.Lexical)));

            Assert.Equal(2, hits.Count);
            Assert.Contains(hits, h => h.Memory.Id == nativeMemory.Id);
            Assert.Contains(hits, h => h.Memory.Metadata?.RootElement.GetProperty("source").GetString() == "knowledge_pack");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecallStore_UnmountedPackIsRemovedFromRecall()
    {
        var root = Path.Combine(Path.GetTempPath(), "banyan-recall-unmount-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var packPath = await CreatePackAsync(root);
            var registry = new FileKnowledgePackMountRegistry(Path.Combine(root, "mounts.json"));
            await registry.MountAsync(packPath, "user:alice", mountedBy: "test");
            await registry.UnmountAsync("com.company-a.products", "user:alice");
            var store = new KnowledgePackRecallStore(new StubMemoryStore(), registry);

            var hits = await CollectAsync(store.SearchAsync(new SearchQuery("product", 5, "user:alice", SearchMode.Lexical)));

            Assert.Empty(hits);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecallStore_NativeOnlyRecallStillWorks()
    {
        var root = Path.Combine(Path.GetTempPath(), "banyan-recall-native-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var nativeMemory = new Memory(
                MemoryId.New(),
                EventId.New(),
                "user:alice",
                "Native memory",
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var store = new KnowledgePackRecallStore(
                new StubMemoryStore([new SearchHit(nativeMemory, 1, null, 1)]),
                new FileKnowledgePackMountRegistry(Path.Combine(root, "mounts.json")));

            var hits = await CollectAsync(store.SearchAsync(new SearchQuery("native", 5, "user:alice", SearchMode.Lexical)));

            var hit = Assert.Single(hits);
            Assert.Equal(nativeMemory.Id, hit.Memory.Id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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

    private static async Task<string> CreatePackAsync(string root)
    {
        var docs = Path.Combine(root, "docs");
        Directory.CreateDirectory(docs);
        await File.WriteAllTextAsync(Path.Combine(docs, "overview.md"), "# Product\nA useful product.");

        var build = await KnowledgePackBuilder.BuildFromPathAsync(
            docs,
            new KnowledgePackBuildOptions
            {
                PackId = "com.company-a.products",
                Name = "Company A Product Knowledge",
                Version = "2026.05"
            });

        var packPath = Path.Combine(root, "company-a.banyanpack");
        await using var stream = File.Create(packPath);
        await KnowledgePackArchive.WriteAsync(stream, build.Manifest, build.Entries);
        return packPath;
    }

    private static async Task<IReadOnlyList<SearchHit>> CollectAsync(IAsyncEnumerable<SearchHit> hits)
    {
        var result = new List<SearchHit>();
        await foreach (var hit in hits)
        {
            result.Add(hit);
        }

        return result;
    }

    private sealed class StubMemoryStore(IReadOnlyList<SearchHit>? hits = null) : IMemoryStore
    {
        public Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
            => Task.FromResult(MemoryId.New());

        public Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
            => Task.FromResult(EventId.New());

        public Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(EventId.New());

        public Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
            => Task.FromResult<Memory?>(null);

        public Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Memory>>([]);

        public async IAsyncEnumerable<SearchHit> SearchAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var hit in hits ?? [])
            {
                ct.ThrowIfCancellationRequested();
                yield return hit;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<MemoryEvent> TraceAsync(MemoryId id, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
