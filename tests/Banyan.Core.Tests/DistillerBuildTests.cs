// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public sealed class DistillerBuildTests
{
    // ── source adapter tests ───────────────────────────────────────────────

    [Fact]
    public async Task Build_IncludesMarkdownSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("overview.md"),
            "# Product\nThis is a product overview.", ct);

        var result = await BuildAsync(dir.Root, ct);

        var src = Assert.Single(result.Sources);
        Assert.Equal("overview.md", src.Path);
        Assert.Equal("text/markdown", src.MediaType);
        Assert.Contains(result.Memories, m => m.Content.Contains("This is a product overview."));
    }

    [Fact]
    public async Task Build_IncludesPlainTextSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("notes.txt"), "Quarterly revenue up 12%.", ct);

        var result = await BuildAsync(dir.Root, ct);

        var src = Assert.Single(result.Sources);
        Assert.Equal("notes.txt", src.Path);
        Assert.Equal("text/plain", src.MediaType);
    }

    [Fact]
    public async Task Build_IncludesCsvSource()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("data.csv"), "id,name\n1,Alice\n2,Bob", ct);

        var result = await BuildAsync(dir.Root, ct);

        var src = Assert.Single(result.Sources);
        Assert.Equal("data.csv", src.Path);
        Assert.Equal("text/csv", src.MediaType);
    }

    [Fact]
    public async Task Build_IgnoresUnsupportedFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("readme.md"), "# Readme", ct);
        await File.WriteAllTextAsync(dir.GetPath("image.png"), "not-real-png", ct);
        await File.WriteAllBytesAsync(dir.GetPath("binary.bin"), [0x00, 0xFF], ct);

        var result = await BuildAsync(dir.Root, ct);

        Assert.Single(result.Sources);
        Assert.Equal("readme.md", result.Sources[0].Path);
    }

    [Fact]
    public async Task Build_AssignsStableRecordIds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("a.md"), "# A", ct);

        var r1 = await BuildAsync(dir.Root, ct);
        var r2 = await BuildAsync(dir.Root, ct);

        Assert.Equal(r1.Memories[0].RecordId, r2.Memories[0].RecordId);
        Assert.Equal(r1.Sources[0].SourceId, r2.Sources[0].SourceId);
    }

    // ── archive output tests ───────────────────────────────────────────────

    [Fact]
    public async Task Build_ProducesValidZipWithRequiredEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("doc.md"), "# Doc\nContent here.", ct);

        var result = await BuildAsync(dir.Root, ct);

        Assert.Contains(result.Entries, e => e.Path == "memories/records.jsonl");
        Assert.Contains(result.Entries, e => e.Path == "sources/sources.jsonl");
        Assert.Contains(result.Entries, e => e.Path == "review/queue.jsonl");
    }

    [Fact]
    public async Task Build_ManifestPassesArchiveValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("doc.md"), "# Doc", ct);

        var result = await BuildAsync(dir.Root, ct);

        await using var ms = new MemoryStream();
        await KnowledgePackArchive.WriteAsync(ms, result.Manifest, result.Entries, ct);
        ms.Position = 0;

        var manifest = await KnowledgePackArchive.ReadManifestAsync(ms, ct);
        Assert.Equal("test.distiller.pack", manifest.PackId);
    }

    [Fact]
    public async Task Build_RecordsJsonlIsParseableFromArchive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("facts.md"), "Key fact: the sky is blue.", ct);

        var result = await BuildAsync(dir.Root, ct);

        await using var ms = new MemoryStream();
        await KnowledgePackArchive.WriteAsync(ms, result.Manifest, result.Entries, ct);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("memories/records.jsonl");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);

        // Each line must be a valid JSON object with record_id, content, source_path.
        foreach (var line in lines)
        {
            var obj = JsonSerializer.Deserialize<JsonElement>(line);
            Assert.True(obj.TryGetProperty("record_id", out _));
            Assert.True(obj.TryGetProperty("content", out var contentProp));
            Assert.Contains("sky is blue", contentProp.GetString() ?? "");
        }
    }

    [Fact]
    public async Task Build_ReviewQueueHasOneEntryPerMemory()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("a.md"), "# A", ct);
        await File.WriteAllTextAsync(dir.GetPath("b.txt"), "B", ct);

        var result = await BuildAsync(dir.Root, ct);

        Assert.Equal(result.Memories.Count, result.ReviewQueue.Count);
        Assert.All(result.ReviewQueue, e => Assert.Equal("accept", e.Decision));
        Assert.All(result.ReviewQueue, e => Assert.NotEmpty(e.RecordId));
    }

    [Fact]
    public async Task Build_ReviewQueueJsonlIsParseableFromArchive()
    {
        var ct = TestContext.Current.CancellationToken;
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.GetPath("doc.md"), "# Doc", ct);

        var result = await BuildAsync(dir.Root, ct);

        await using var ms = new MemoryStream();
        await KnowledgePackArchive.WriteAsync(ms, result.Manifest, result.Entries, ct);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("review/queue.jsonl");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var line = await reader.ReadLineAsync(ct);
        Assert.NotNull(line);
        var obj = JsonSerializer.Deserialize<JsonElement>(line);
        Assert.True(obj.TryGetProperty("record_id", out _));
        Assert.True(obj.TryGetProperty("decision", out var decision));
        Assert.Equal("accept", decision.GetString());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static Task<KnowledgePackBuildResult> BuildAsync(string root, CancellationToken ct)
        => KnowledgePackBuilder.BuildFromPathAsync(root,
            new KnowledgePackBuildOptions
            {
                PackId  = "test.distiller.pack",
                Name    = "Test Distiller Pack",
                Version = "0.1.0",
            }, ct);

    private sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "banyan-distiller-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string GetPath(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
