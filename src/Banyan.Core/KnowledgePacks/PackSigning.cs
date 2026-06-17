// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banyan.Core.KnowledgePacks;

/// <summary>
/// Signs/verifies a <c>.banyanpack</c> v2 (KB-2). Pluggable so Lite/Pro/Ent can
/// use a node Ed25519 key / HSM; the implementation lives outside Banyan.Core
/// (which stays zero-dependency) and is injected. Sign over the canonical pack
/// digest produced by <see cref="PackSigning.ComputeDigest"/>.
/// </summary>
public interface IPackSigner
{
    string Algorithm { get; }
    string? KeyId { get; }
    string Sign(string digest);
    bool Verify(string digest, string signature, string? keyId);
}

/// <summary>The detached signature stored as <c>manifest.sig</c> in a v2 pack.</summary>
public sealed record PackSignature(
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("key_id")] string? KeyId,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("signature")] string Signature);

public enum PackVerification { Valid, Unsigned, DigestMismatch, BadSignature }

/// <summary>
/// Canonical digest + signature helpers for <c>.banyanpack</c> v2 (KB-2). The digest
/// binds every archive entry except <c>manifest.sig</c> itself: entries are hashed
/// (SHA-256), sorted by path, and the per-entry <c>path\nhash</c> lines are hashed
/// again. Tampering with any file or path changes the digest, so a stored signature
/// no longer verifies.
/// </summary>
public static class PackSigning
{
    public const string SignaturePath = "manifest.sig";
    public const string SourcesPrefix = "sources/";
    public const string EmbeddingsPrefix = "embeddings/";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Computes the canonical digest over all entries except the signature file.</summary>
    public static async Task<string> ComputeDigestAsync(ZipArchive archive, CancellationToken ct = default)
    {
        var lines = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == SignaturePath) continue;
            await using var s = entry.Open();
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(await sha.ComputeHashAsync(s, ct)).ToLowerInvariant();
            lines.Add($"{entry.FullName}\n{hash}");
        }
        lines.Sort(StringComparer.Ordinal);
        var canonical = string.Join("\n", lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Computes the digest over a pack stream (leaves the stream readable from start).</summary>
    public static async Task<string> ComputeDigestAsync(Stream packStream, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(packStream, ZipArchiveMode.Read, leaveOpen: true);
        return await ComputeDigestAsync(archive, ct);
    }

    /// <summary>Signs an existing pack stream in place, appending <c>manifest.sig</c>.</summary>
    public static async Task SignAsync(Stream packStream, IPackSigner signer, CancellationToken ct = default)
    {
        string digest;
        using (var read = new ZipArchive(packStream, ZipArchiveMode.Read, leaveOpen: true))
            digest = await ComputeDigestAsync(read, ct);

        var sig = new PackSignature(signer.Algorithm, signer.KeyId, digest, signer.Sign(digest));
        using var write = new ZipArchive(packStream, ZipArchiveMode.Update, leaveOpen: true);
        write.GetEntry(SignaturePath)?.Delete();
        var entry = write.CreateEntry(SignaturePath, CompressionLevel.NoCompression);
        await using var es = entry.Open();
        await JsonSerializer.SerializeAsync(es, sig, JsonOptions, ct);
    }

    /// <summary>Reads the detached <c>manifest.sig</c> from a pack, or null when unsigned.</summary>
    public static async Task<PackSignature?> ReadSignatureAsync(Stream packStream, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(packStream, ZipArchiveMode.Read, leaveOpen: true);
        var sigEntry = archive.GetEntry(SignaturePath);
        if (sigEntry is null) return null;
        await using var s = sigEntry.Open();
        return await JsonSerializer.DeserializeAsync<PackSignature>(s, JsonOptions, ct);
    }

    /// <summary>Verifies a pack's signature against its recomputed digest.</summary>
    public static async Task<PackVerification> VerifyAsync(
        Stream packStream, IPackSigner signer, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(packStream, ZipArchiveMode.Read, leaveOpen: true);
        var sigEntry = archive.GetEntry(SignaturePath);
        if (sigEntry is null) return PackVerification.Unsigned;

        PackSignature? sig;
        await using (var s = sigEntry.Open())
            sig = await JsonSerializer.DeserializeAsync<PackSignature>(s, JsonOptions, ct);
        if (sig is null) return PackVerification.BadSignature;

        var digest = await ComputeDigestAsync(archive, ct);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(digest), Encoding.UTF8.GetBytes(sig.Digest)))
            return PackVerification.DigestMismatch;

        return signer.Verify(digest, sig.Signature, sig.KeyId)
            ? PackVerification.Valid
            : PackVerification.BadSignature;
    }
}
