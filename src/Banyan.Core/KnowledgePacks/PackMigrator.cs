// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text.Json;

namespace Banyan.Core.KnowledgePacks;

/// <summary>
/// Upgrades a legacy (v1) <c>.banyanpack</c> to the v2 format (KB-3): copies all
/// content entries, bumps <c>manifest.format_version</c> to 2, and drops any stale
/// <c>manifest.sig</c> (a v1 signature would not cover the new manifest). The
/// caller signs the result with an <see cref="IPackSigner"/> afterwards. Embeddings
/// are not synthesized here — that is the Distiller's job (KB-4); migrate preserves
/// whatever the pack already contains.
/// </summary>
public static class PackMigrator
{
    public static async Task MigrateToV2Async(Stream source, Stream destination, CancellationToken ct = default)
    {
        using var src = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        using var dst = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var entry in src.Entries)
        {
            if (entry.FullName == PackSigning.SignaturePath)
                continue; // stale signature — re-sign after migrate

            if (entry.FullName == KnowledgePackArchive.ManifestPath)
            {
                await using var ms = entry.Open();
                var manifest = await JsonSerializer.DeserializeAsync<KnowledgePackManifest>(
                    ms, KnowledgePackArchive.JsonOptions, ct)
                    ?? throw new InvalidOperationException("pack has no readable manifest.json");

                var upgraded = manifest with { FormatVersion = 2 };
                var outEntry = dst.CreateEntry(KnowledgePackArchive.ManifestPath, CompressionLevel.Optimal);
                await using var os = outEntry.Open();
                await JsonSerializer.SerializeAsync(os, upgraded, KnowledgePackArchive.JsonOptions, ct);
                continue;
            }

            var copy = dst.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            await using var inS = entry.Open();
            await using var outS = copy.Open();
            await inS.CopyToAsync(outS, ct);
        }
    }
}
