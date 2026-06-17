// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public class PackMigratorTests
{
    private static MemoryStream BuildV1Pack()
    {
        var manifest = new KnowledgePackManifest
        {
            PackId = "p1", Name = "Legacy", Version = "1.0.0", CreatedAt = DateTimeOffset.UnixEpoch,
            PackType = "knowledge", ContentTypes = ["text"], TargetScopes = ["default"],
            // FormatVersion left default (1)
        };
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var m = zip.CreateEntry(KnowledgePackArchive.ManifestPath, CompressionLevel.Optimal);
            using (var s = m.Open()) JsonSerializer.Serialize(s, manifest, KnowledgePackArchive.JsonOptions);
            var r = zip.CreateEntry("memories/records.jsonl", CompressionLevel.Optimal);
            using (var s = r.Open()) s.Write(Encoding.UTF8.GetBytes("rec-1\nrec-2"));
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Migrate_BumpsFormatVersion_PreservesContent()
    {
        using var v1 = BuildV1Pack();
        using var v2 = new MemoryStream();
        await PackMigrator.MigrateToV2Async(v1, v2);
        v2.Position = 0;

        using var zip = new ZipArchive(v2, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await JsonSerializer.DeserializeAsync<KnowledgePackManifest>(
            zip.GetEntry(KnowledgePackArchive.ManifestPath)!.Open(), KnowledgePackArchive.JsonOptions);
        Assert.Equal(2, manifest!.FormatVersion);

        await using var rs = zip.GetEntry("memories/records.jsonl")!.Open();
        Assert.Equal("rec-1\nrec-2", await new StreamReader(rs).ReadToEndAsync());
    }

    [Fact]
    public async Task Migrated_Pack_CanBeSignedAndVerified()
    {
        using var v1 = BuildV1Pack();
        using var v2 = new MemoryStream();
        await PackMigrator.MigrateToV2Async(v1, v2);

        var signer = new StubSigner();
        await PackSigning.SignAsync(v2, signer);
        Assert.Equal(PackVerification.Valid, await PackSigning.VerifyAsync(v2, signer));
    }

    private sealed class StubSigner : IPackSigner
    {
        public string Algorithm => "stub";
        public string? KeyId => "k";
        public string Sign(string digest) => "sig:" + digest;
        public bool Verify(string digest, string signature, string? keyId) => signature == "sig:" + digest;
    }
}
