// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public sealed class PackVersionManagementTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"banyan-ver-{Guid.NewGuid():N}");
    private FileKnowledgePackMountRegistry _registry = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _registry = new FileKnowledgePackMountRegistry(Path.Combine(_dir, "mounts.json"));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() { try { Directory.Delete(_dir, true); } catch { } return ValueTask.CompletedTask; }

    private async Task<string> BuildPackAsync(string packId, string version)
    {
        var manifest = new KnowledgePackManifest
        {
            PackId = packId, Name = packId, Version = version, CreatedAt = DateTimeOffset.UnixEpoch,
            PackType = "knowledge", ContentTypes = ["text"], TargetScopes = ["default"],
        };
        var path = Path.Combine(_dir, $"{packId}-{version}.banyanpack");
        await using var fs = File.Create(path);
        await KnowledgePackArchive.WriteAsync(fs, manifest);
        return path;
    }

    [Fact]
    public async Task Upgrade_Rollback_Pin_FlowAcrossVersions()
    {
        await _registry.MountAsync(await BuildPackAsync("pack-kb5", "1.0.0"), "default");
        await _registry.MountAsync(await BuildPackAsync("pack-kb5", "2.0.0"), "default");

        // Upgrade: activate 2.0.0, deactivate siblings.
        Assert.True(await _registry.SetActiveVersionAsync("default", "pack-kb5", "2.0.0"));
        var versions = await _registry.ListVersionsAsync("default", "pack-kb5");
        Assert.Equal(2, versions.Count);
        Assert.True(versions.Single(v => v.PackVersion == "2.0.0").Enabled);
        Assert.False(versions.Single(v => v.PackVersion == "1.0.0").Enabled);

        // Rollback: re-activate 1.0.0.
        Assert.True(await _registry.SetActiveVersionAsync("default", "pack-kb5", "1.0.0"));
        versions = await _registry.ListVersionsAsync("default", "pack-kb5");
        Assert.True(versions.Single(v => v.PackVersion == "1.0.0").Enabled);
        Assert.False(versions.Single(v => v.PackVersion == "2.0.0").Enabled);

        // Pin the active version.
        Assert.True(await _registry.SetPinnedAsync("default", "pack-kb5", "1.0.0", pinned: true));
        Assert.True((await _registry.ListVersionsAsync("default", "pack-kb5"))
            .Single(v => v.PackVersion == "1.0.0").Pinned);
    }

    [Fact]
    public async Task SetActiveVersion_UnknownVersion_ReturnsFalse()
    {
        await _registry.MountAsync(await BuildPackAsync("pack-kb5", "1.0.0"), "default");
        Assert.False(await _registry.SetActiveVersionAsync("default", "pack-kb5", "9.9.9"));
    }
}
