// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Banyan.Core.KnowledgePacks;

public static class KnowledgePackArchive
{
    public const string ManifestPath      = "manifest.json";
    public const string EncryptedPayloadPath = "payload.enc";

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

    /// <summary>
    /// Build an encrypted .banyanpack: outer ZIP contains plaintext manifest.json
    /// (with encryption metadata) + payload.enc (AES-256-GCM-encrypted inner archive).
    /// </summary>
    public static async Task WriteEncryptedAsync(
        Stream output,
        KnowledgePackManifest manifest,
        IEnumerable<KnowledgePackArchiveEntry>? entries = null,
        string passphrase = "",
        int iterations = KnowledgePackEncryption.DefaultIterations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        // 1. Build unencrypted inner archive in memory.
        using var innerStream = new MemoryStream();
        await WriteAsync(innerStream, manifest with { Encryption = null }, entries, cancellationToken).ConfigureAwait(false);
        var innerBytes = innerStream.ToArray();

        // 2. Derive key and encrypt.
        var salt      = KnowledgePackEncryption.GenerateSalt();
        var key       = KnowledgePackEncryption.DeriveKey(passphrase, salt, iterations);
        var encrypted = KnowledgePackEncryption.Encrypt(innerBytes, key);

        // 3. Outer archive: manifest (with encryption field) + payload.enc.
        var encManifest = manifest with
        {
            Encryption = new KnowledgePackEncryptionMetadata
            {
                Algorithm  = KnowledgePackEncryption.AlgorithmAes256Gcm,
                Kdf        = KnowledgePackEncryption.KdfPbkdf2Sha256,
                Salt       = Convert.ToBase64String(salt),
                Iterations = iterations,
            }
        };

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonEntryAsync(archive, ManifestPath, encManifest, cancellationToken).ConfigureAwait(false);
        var payloadEntry  = archive.CreateEntry(EncryptedPayloadPath, CompressionLevel.NoCompression);
        await using var payloadStream = payloadEntry.Open();
        await payloadStream.WriteAsync(encrypted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decrypt an encrypted .banyanpack and return a stream containing the plaintext inner archive.
    /// Throws <see cref="KnowledgePackWrongPassphraseException"/> if the passphrase is wrong.
    /// </summary>
    public static async Task<MemoryStream> DecryptAsync(
        Stream input,
        string passphrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        using var archive  = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var manifest       = await ReadManifestAsync(archive, cancellationToken).ConfigureAwait(false);

        var enc = manifest.Encryption
            ?? throw new InvalidOperationException("Pack is not encrypted.");

        var salt = Convert.FromBase64String(enc.Salt);
        var key  = KnowledgePackEncryption.DeriveKey(passphrase, salt, enc.Iterations);

        var payloadEntry = archive.GetEntry(EncryptedPayloadPath)
            ?? throw new InvalidDataException("Encrypted pack is missing payload.enc.");

        using var payloadStream = payloadEntry.Open();
        using var ms = new MemoryStream();
        await payloadStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        byte[] decrypted;
        try
        {
            decrypted = KnowledgePackEncryption.Decrypt(ms.ToArray(), key);
        }
        catch (CryptographicException ex)
        {
            throw new KnowledgePackWrongPassphraseException(
                "Wrong passphrase or corrupted pack — authentication tag mismatch.", ex);
        }

        return new MemoryStream(decrypted, writable: false);
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
