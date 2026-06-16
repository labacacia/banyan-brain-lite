using Banyan.Core;

namespace Banyan.Lite;

public sealed record LiteHealthReport(string Status, IReadOnlyDictionary<string, LiteHealthCheck> Checks);

public sealed record LiteHealthCheck(
    string Status,
    string? Message = null,
    string? ModelId = null,
    int? Dimensions = null);

public static class LiteHealthProbe
{
    /// <summary>Disk-free threshold below which the resources check reports degraded.</summary>
    public const long MinFreeDiskBytes = 100L * 1024 * 1024;

    public static async Task<LiteHealthReport> CheckAsync(
        SqliteMemoryStore store,
        IEmbedder embedder,
        CancellationToken ct = default)
        => await CheckAsync(store, embedder, dbPath: null, ct);

    /// <summary>
    /// Health report including a resources check (OBS-3): process memory, and — when
    /// <paramref name="dbPath"/> is given — the memory DB file size and free disk space.
    /// </summary>
    public static async Task<LiteHealthReport> CheckAsync(
        SqliteMemoryStore store,
        IEmbedder embedder,
        string? dbPath,
        CancellationToken ct = default)
    {
        var checks = new Dictionary<string, LiteHealthCheck>(StringComparer.Ordinal)
        {
            ["sqlite"] = await CheckSqliteAsync(store, ct),
            ["embedder"] = await CheckEmbedderAsync(embedder, ct),
            ["resources"] = CheckResources(dbPath),
        };

        var status = checks.Values.All(c => c.Status == "ok") ? "ok" : "degraded";
        return new LiteHealthReport(status, checks);
    }

    public static LiteHealthCheck CheckResources(string? dbPath)
    {
        try
        {
            var rssMb = Environment.WorkingSet / (1024 * 1024);
            long dbMb = 0;
            long freeMb = -1;
            var degraded = false;

            if (!string.IsNullOrWhiteSpace(dbPath))
            {
                var full = Path.GetFullPath(dbPath);
                if (File.Exists(full))
                    dbMb = new FileInfo(full).Length / (1024 * 1024);

                var root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        freeMb = drive.AvailableFreeSpace / (1024 * 1024);
                        degraded = drive.AvailableFreeSpace < MinFreeDiskBytes;
                    }
                }
            }

            var msg = $"rss={rssMb}MB, db={dbMb}MB, disk_free={(freeMb < 0 ? "n/a" : freeMb + "MB")}";
            return new LiteHealthCheck(degraded ? "degraded" : "ok", msg);
        }
        catch (Exception ex)
        {
            return new LiteHealthCheck("degraded", ex.Message);
        }
    }

    private static async Task<LiteHealthCheck> CheckSqliteAsync(SqliteMemoryStore store, CancellationToken ct)
    {
        try
        {
            await store.PingAsync(ct);
            return new LiteHealthCheck("ok");
        }
        catch (Exception ex)
        {
            return new LiteHealthCheck("degraded", ex.Message);
        }
    }

    private static async Task<LiteHealthCheck> CheckEmbedderAsync(IEmbedder embedder, CancellationToken ct)
    {
        try
        {
            var vector = await embedder.EmbedQueryAsync("banyan health probe", ct);
            if (vector.Length != embedder.Dimensions)
                return new LiteHealthCheck(
                    "degraded",
                    $"Embedder returned {vector.Length} dimensions; expected {embedder.Dimensions}.",
                    embedder.ModelId,
                    embedder.Dimensions);

            return new LiteHealthCheck("ok", ModelId: embedder.ModelId, Dimensions: embedder.Dimensions);
        }
        catch (Exception ex)
        {
            return new LiteHealthCheck("degraded", ex.Message, embedder.ModelId, embedder.Dimensions);
        }
    }
}
