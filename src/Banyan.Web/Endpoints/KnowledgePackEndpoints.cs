using Banyan.Core.KnowledgePacks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Banyan.Web.Endpoints;

public static class KnowledgePackEndpoints
{
    public sealed record PackFileInfo(
        string FileName, string FullPath, long SizeBytes, DateTimeOffset ModifiedAt,
        string? PackId, string? Name, string? Version, bool Encrypted);

    public sealed record MountRequest(
        string PackPath, string Namespace, string? MountedBy = null, string? Passphrase = null);

    public sealed record UnmountRequest(string PackId, string Namespace, string? Version = null);

    public sealed record MountedPackDto(
        string Namespace, string PackId, string PackVersion, string PackName,
        string PackType, string PackPath, string PackChecksum,
        DateTimeOffset MountedAt, string? MountedBy, bool Enabled);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/packs").WithTags("packs").RequireAuthorization();

        // List .banyanpack files in the store directory.
        g.MapGet("/installed", async (WebOptions opts) =>
        {
            var dir = WebOptions.ExpandHome(opts.PackStorePath);
            if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<PackFileInfo>());

            var files = new List<PackFileInfo>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.banyanpack"))
            {
                var info = new FileInfo(file);
                string? packId = null, name = null, version = null;
                var encrypted = false;
                try
                {
                    await using var stream = File.OpenRead(file);
                    var manifest = await KnowledgePackArchive.ReadManifestAsync(stream);
                    packId    = manifest.PackId;
                    name      = manifest.Name;
                    version   = manifest.Version;
                    encrypted = manifest.Encryption is not null;
                }
                catch { /* unreadable / corrupted — still list the file */ }

                files.Add(new PackFileInfo(
                    info.Name, file, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    packId, name, version, encrypted));
            }

            return Results.Ok(files.OrderBy(f => f.FileName));
        });

        // List mounted packs (optionally filtered by namespace).
        g.MapGet("/mounts", async (
            [FromQuery] string? ns,
            FileKnowledgePackMountRegistry registry) =>
        {
            var records = await registry.ListAsync(ns);
            return Results.Ok(records.Select(r => new MountedPackDto(
                r.Namespace, r.PackId, r.PackVersion, r.PackName,
                r.PackType, r.PackPath, r.PackChecksum,
                r.MountedAt, r.MountedBy, r.Enabled)));
        });

        // Upload a .banyanpack file to the store directory.
        g.MapPost("/upload", async (IFormFile file, WebOptions opts) =>
        {
            if (!file.FileName.EndsWith(".banyanpack", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Only .banyanpack files are accepted." });

            var dir = WebOptions.ExpandHome(opts.PackStorePath);
            Directory.CreateDirectory(dir);

            var safeName = Path.GetFileName(file.FileName);
            var destPath = Path.Combine(dir, safeName);
            await using var dest = File.Create(destPath);
            await file.CopyToAsync(dest);

            // Read manifest to return info (works even for encrypted packs — manifest is plaintext).
            dest.Position = 0;
            KnowledgePackManifest? manifest = null;
            try { manifest = await KnowledgePackArchive.ReadManifestAsync(dest); }
            catch { /* accept files that are valid ZIPs but not yet inspectable */ }

            return Results.Ok(new PackFileInfo(
                safeName, destPath, file.Length,
                DateTimeOffset.UtcNow,
                manifest?.PackId, manifest?.Name, manifest?.Version,
                manifest?.Encryption is not null));
        })
        .DisableAntiforgery();

        // Mount a pack from the store into a namespace.
        g.MapPost("/mount", async (
            MountRequest body,
            FileKnowledgePackMountRegistry registry) =>
        {
            try
            {
                var result = await registry.MountAsync(
                    body.PackPath, body.Namespace,
                    mountedBy: body.MountedBy,
                    passphrase: body.Passphrase);

                var r = result.Record;
                return Results.Ok(new
                {
                    created = result.Created,
                    mount   = new MountedPackDto(
                        r.Namespace, r.PackId, r.PackVersion, r.PackName,
                        r.PackType, r.PackPath, r.PackChecksum,
                        r.MountedAt, r.MountedBy, r.Enabled)
                });
            }
            catch (KnowledgePackWrongPassphraseException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex) when (ex is InvalidDataException or KnowledgePackValidationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Unmount a pack.
        g.MapDelete("/mount", async (
            UnmountRequest body,
            FileKnowledgePackMountRegistry registry) =>
        {
            var removed = await registry.UnmountAsync(body.PackId, body.Namespace, body.Version);
            return removed
                ? Results.Ok(new { removed = true })
                : Results.NotFound(new { error = "No matching mount found." });
        });

        // Inspect a pack's manifest (by path).
        g.MapGet("/manifest", async ([FromQuery] string path) =>
        {
            if (!File.Exists(path))
                return Results.NotFound(new { error = "File not found." });
            try
            {
                await using var stream = File.OpenRead(path);
                var manifest = await KnowledgePackArchive.ReadManifestAsync(stream);
                return Results.Ok(manifest);
            }
            catch (Exception ex) when (ex is InvalidDataException or KnowledgePackValidationException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
