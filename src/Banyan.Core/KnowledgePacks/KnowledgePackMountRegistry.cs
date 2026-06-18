// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banyan.Core.KnowledgePacks;

public sealed record KnowledgePackMountRecord
{
    [JsonPropertyName("namespace")]
    public required string Namespace { get; init; }

    [JsonPropertyName("pack_id")]
    public required string PackId { get; init; }

    [JsonPropertyName("pack_version")]
    public required string PackVersion { get; init; }

    [JsonPropertyName("pack_path")]
    public required string PackPath { get; init; }

    [JsonPropertyName("pack_checksum")]
    public required string PackChecksum { get; init; }

    [JsonPropertyName("pack_name")]
    public required string PackName { get; init; }

    [JsonPropertyName("pack_type")]
    public required string PackType { get; init; }

    [JsonPropertyName("mounted_at")]
    public required DateTimeOffset MountedAt { get; init; }

    [JsonPropertyName("mounted_by")]
    public string? MountedBy { get; init; }

    /// <summary>KB-5: pinned versions are excluded from auto-upgrade (advisory; explicit
    /// activation can still override). Pin/upgrade/rollback are operator actions.</summary>
    public bool Pinned { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}

public sealed record KnowledgePackMountResult(KnowledgePackMountRecord Record, bool Created);

public sealed class FileKnowledgePackMountRegistry
{
    private readonly string path;

    public FileKnowledgePackMountRegistry(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<KnowledgePackMountResult> MountAsync(
        string packPath,
        string @namespace,
        string? mountedBy = null,
        string? passphrase = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        var fullPackPath = Path.GetFullPath(packPath);

        // Read manifest from outer archive (always plaintext, even for encrypted packs).
        KnowledgePackManifest manifest;
        await using (var peekStream = File.OpenRead(fullPackPath))
            manifest = await KnowledgePackArchive.ReadManifestAsync(peekStream, cancellationToken).ConfigureAwait(false);

        // If encrypted: validate passphrase and write decrypted copy to local cache.
        // The registry points to the decrypted copy so KnowledgePackRecallStore can
        // read it without needing the passphrase again at search time.
        string resolvedPackPath = fullPackPath;
        if (manifest.Encryption is not null)
        {
            if (string.IsNullOrEmpty(passphrase))
                throw new ArgumentException("Pack is encrypted — passphrase is required to mount.", nameof(passphrase));

            var cacheDir = Path.Combine(Path.GetDirectoryName(path)!, "cache",
                manifest.PackId, manifest.Version);
            Directory.CreateDirectory(cacheDir);
            var cachePath = Path.Combine(cacheDir, Path.GetFileName(fullPackPath));

            await using var encStream = File.OpenRead(fullPackPath);
            using var decrypted = await KnowledgePackArchive.DecryptAsync(encStream, passphrase, cancellationToken).ConfigureAwait(false);
            await using var cacheFile = File.Create(cachePath);
            decrypted.Position = 0;
            await decrypted.CopyToAsync(cacheFile, cancellationToken).ConfigureAwait(false);

            // Re-read manifest from decrypted inner archive (canonical form, no encryption field).
            await cacheFile.FlushAsync(cancellationToken).ConfigureAwait(false);
            cacheFile.Position = 0;
            manifest = await KnowledgePackArchive.ReadManifestAsync(cacheFile, cancellationToken).ConfigureAwait(false);

            resolvedPackPath = cachePath;
        }

        await using var packStream = File.OpenRead(resolvedPackPath);
        var checksum = "sha256:" + Convert.ToHexString(
            await SHA256.HashDataAsync(packStream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();

        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var existingIndex = records.FindIndex(r =>
            string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)
            && string.Equals(r.PackId, manifest.PackId, StringComparison.Ordinal)
            && string.Equals(r.PackVersion, manifest.Version, StringComparison.Ordinal));

        var record = new KnowledgePackMountRecord
        {
            Namespace = @namespace,
            PackId = manifest.PackId,
            PackVersion = manifest.Version,
            PackPath = resolvedPackPath,
            PackChecksum = checksum,
            PackName = manifest.Name,
            PackType = manifest.PackType,
            MountedAt = DateTimeOffset.UtcNow,
            MountedBy = mountedBy,
            Enabled = true
        };

        var created = existingIndex < 0;
        if (created)
        {
            records.Add(record);
        }
        else
        {
            var existing = records[existingIndex];
            records[existingIndex] = record with
            {
                MountedAt = existing.MountedAt,
                MountedBy = mountedBy ?? existing.MountedBy
            };
        }

        await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        return new KnowledgePackMountResult(records[created ? ^1 : existingIndex], created);
    }

    public async Task<IReadOnlyList<KnowledgePackMountRecord>> ListAsync(
        string? @namespace = null,
        CancellationToken cancellationToken = default)
    {
        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(@namespace)
            ? records.OrderBy(static r => r.Namespace, StringComparer.Ordinal).ThenBy(static r => r.PackId, StringComparer.Ordinal).ToList()
            : records.Where(r => string.Equals(r.Namespace, @namespace, StringComparison.Ordinal))
                .OrderBy(static r => r.PackId, StringComparer.Ordinal)
                .ToList();
    }

    public async Task<bool> UnmountAsync(
        string packId,
        string @namespace,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var removed = records.RemoveAll(r =>
            string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)
            && string.Equals(r.PackId, packId, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(version) || string.Equals(r.PackVersion, version, StringComparison.Ordinal)));

        if (removed > 0)
        {
            await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        }

        return removed > 0;
    }

    /// <summary>All mounted versions of a pack in a namespace (KB-5 version management).</summary>
    public async Task<IReadOnlyList<KnowledgePackMountRecord>> ListVersionsAsync(
        string @namespace, string packId, CancellationToken cancellationToken = default)
    {
        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return records
            .Where(r => string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)
                     && string.Equals(r.PackId, packId, StringComparison.Ordinal))
            .OrderBy(static r => r.PackVersion, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Makes one version the active (enabled) one for a pack in a namespace and
    /// disables its siblings — the primitive behind upgrade and rollback (KB-5).</summary>
    public async Task<bool> SetActiveVersionAsync(
        string @namespace, string packId, string version, CancellationToken cancellationToken = default)
    {
        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var siblings = records.Where(r =>
            string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)
            && string.Equals(r.PackId, packId, StringComparison.Ordinal)).ToList();
        if (!siblings.Any(r => string.Equals(r.PackVersion, version, StringComparison.Ordinal)))
            return false; // that version is not mounted

        var changed = false;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (!string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)
                || !string.Equals(r.PackId, packId, StringComparison.Ordinal))
                continue;
            var enable = string.Equals(r.PackVersion, version, StringComparison.Ordinal);
            if (r.Enabled != enable)
            {
                records[i] = r with { Enabled = enable };
                changed = true;
            }
        }
        if (changed) await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Pins/unpins a specific mounted version (KB-5; advisory for auto-upgrade).</summary>
    public async Task<bool> SetPinnedAsync(
        string @namespace, string packId, string version, bool pinned, CancellationToken cancellationToken = default)
    {
        var records = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var idx = records.FindIndex(r =>
            string.Equals(r.Namespace, @namespace, StringComparison.Ordinal)
            && string.Equals(r.PackId, packId, StringComparison.Ordinal)
            && string.Equals(r.PackVersion, version, StringComparison.Ordinal));
        if (idx < 0) return false;
        if (records[idx].Pinned != pinned)
        {
            records[idx] = records[idx] with { Pinned = pinned };
            await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<List<KnowledgePackMountRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var records = await JsonSerializer.DeserializeAsync<List<KnowledgePackMountRecord>>(
            stream,
            KnowledgePackArchive.JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return records ?? [];
    }

    private async Task SaveAsync(List<KnowledgePackMountRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, records, KnowledgePackArchive.JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
