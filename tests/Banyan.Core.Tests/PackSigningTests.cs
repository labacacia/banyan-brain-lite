// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public class PackSigningTests
{
    private static MemoryStream BuildPack((string path, string content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var e = zip.CreateEntry(path, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Sign_ThenVerify_Valid()
    {
        var signer = new HmacPackSigner("k1");
        using var pack = BuildPack([("manifest.json", "{}"), ("memories/records.jsonl", "a\nb")]);
        await PackSigning.SignAsync(pack, signer);
        Assert.Equal(PackVerification.Valid, await PackSigning.VerifyAsync(pack, signer));
    }

    [Fact]
    public async Task Tamper_AfterSign_DigestMismatch()
    {
        var signer = new HmacPackSigner("k1");
        using var pack = BuildPack([("manifest.json", "{}"), ("memories/records.jsonl", "a\nb")]);
        await PackSigning.SignAsync(pack, signer);

        // Mutate a content entry after signing.
        using (var zip = new ZipArchive(pack, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("memories/records.jsonl")!.Delete();
            var e = zip.CreateEntry("memories/records.jsonl", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(Encoding.UTF8.GetBytes("a\nTAMPERED"));
        }
        pack.Position = 0;

        Assert.Equal(PackVerification.DigestMismatch, await PackSigning.VerifyAsync(pack, signer));
    }

    [Fact]
    public async Task WrongSigner_BadSignature()
    {
        using var pack = BuildPack([("manifest.json", "{}")]);
        await PackSigning.SignAsync(pack, new HmacPackSigner("k1"));
        Assert.Equal(PackVerification.BadSignature, await PackSigning.VerifyAsync(pack, new HmacPackSigner("k2")));
    }

    [Fact]
    public async Task UnsignedPack_ReportsUnsigned()
    {
        using var pack = BuildPack([("manifest.json", "{}")]);
        Assert.Equal(PackVerification.Unsigned, await PackSigning.VerifyAsync(pack, new HmacPackSigner("k1")));
    }

    [Fact]
    public void ManifestV2_FieldsDefaultAndRoundTrip()
    {
        var m = new KnowledgePackManifest
        {
            PackId = "p1", Name = "n", Version = "1.0.0", CreatedAt = DateTimeOffset.UnixEpoch,
            PackType = "knowledge", ContentTypes = ["text"], TargetScopes = ["default"],
            FormatVersion = 2, EmbedderProfile = "bge-small-zh-v1.5",
        };
        var json = System.Text.Json.JsonSerializer.Serialize(m);
        Assert.Contains("\"format_version\":2", json);
        Assert.Contains("bge-small-zh-v1.5", json);
        // v1 default
        Assert.Equal(1, new KnowledgePackManifest
        {
            PackId = "p", Name = "n", Version = "1", CreatedAt = DateTimeOffset.UnixEpoch,
            PackType = "knowledge", ContentTypes = ["text"], TargetScopes = ["default"],
        }.FormatVersion);
    }

    // HMAC stand-in for the real Ed25519 signer (wired by editions in KB-3).
    private sealed class HmacPackSigner(string key) : IPackSigner
    {
        public string Algorithm => "hmac-sha256";
        public string? KeyId => "test";
        public string Sign(string digest)
        {
            using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(digest)));
        }
        public bool Verify(string digest, string signature, string? keyId) => Sign(digest) == signature;
    }
}
