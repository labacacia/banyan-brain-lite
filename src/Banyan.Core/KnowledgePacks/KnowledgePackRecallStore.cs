using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Banyan.Core.KnowledgePacks;

public sealed class KnowledgePackRecallStore(
    IMemoryStore inner,
    FileKnowledgePackMountRegistry mountRegistry) : IMemoryStore
{
    private const double PackScoreBase = 0.5;

    public Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
        => inner.WriteAsync(req, ct);

    public Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
        => inner.UpdateAsync(id, req, ct);

    public Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
        => inner.ForgetAsync(id, reason, ct);

    public Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
        => inner.GetAsync(id, ct);

    public Task<IReadOnlyList<Memory>> RecallAsync(IEnumerable<MemoryId> ids, CancellationToken ct = default)
        => inner.RecallAsync(ids, ct);

    public async IAsyncEnumerable<SearchHit> SearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var hits = new List<SearchHit>();
        await foreach (var hit in inner.SearchAsync(query, ct).ConfigureAwait(false))
        {
            hits.Add(hit);
        }

        await foreach (var hit in SearchMountedPacksAsync(query, ct).ConfigureAwait(false))
        {
            hits.Add(hit);
        }

        foreach (var hit in hits.OrderByDescending(static h => h.Score).Take(query.K))
        {
            yield return hit;
        }
    }

    public IAsyncEnumerable<MemoryEvent> TraceAsync(MemoryId id, CancellationToken ct = default)
        => inner.TraceAsync(id, ct);

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    private async IAsyncEnumerable<SearchHit> SearchMountedPacksAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            yield break;
        }

        var mounted = await mountRegistry.ListAsync(query.Namespace, ct).ConfigureAwait(false);
        foreach (var mount in mounted.Where(static m => m.Enabled))
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(mount.PackPath))
            {
                continue;
            }

            await using var stream = File.OpenRead(mount.PackPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var manifest = await KnowledgePackArchive.ReadManifestAsync(archive, ct).ConfigureAwait(false);
            if (!manifest.Permissions.AllowRecall)
            {
                continue;
            }

            var rank = 0;
            var sources = await ReadSourceRecordsAsync(archive, ct).ConfigureAwait(false);
            foreach (var record in await ReadMemoryRecordsAsync(archive, ct).ConfigureAwait(false))
            {
                var score = Score(query.Text, record);
                if (score <= 0)
                {
                    continue;
                }

                rank++;
                yield return new SearchHit(
                    ToMemory(mount, manifest, record, sources.GetValueOrDefault(record.SourceId)),
                    score,
                    VectorRank: null,
                    LexicalRank: rank);
            }
        }
    }

    private static async Task<IReadOnlyList<KnowledgePackMemoryRecord>> ReadMemoryRecordsAsync(
        ZipArchive archive,
        CancellationToken ct)
    {
        var entry = archive.GetEntry("memories/records.jsonl");
        if (entry is null)
        {
            return [];
        }

        var records = new List<KnowledgePackMemoryRecord>();
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<KnowledgePackMemoryRecord>(
                line,
                KnowledgePackArchive.JsonLineOptions);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private static async Task<IReadOnlyDictionary<string, KnowledgePackSourceRecord>> ReadSourceRecordsAsync(
        ZipArchive archive,
        CancellationToken ct)
    {
        var entry = archive.GetEntry("sources/sources.jsonl");
        if (entry is null)
        {
            return new Dictionary<string, KnowledgePackSourceRecord>(StringComparer.Ordinal);
        }

        var sources = new Dictionary<string, KnowledgePackSourceRecord>(StringComparer.Ordinal);
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var source = JsonSerializer.Deserialize<KnowledgePackSourceRecord>(
                line,
                KnowledgePackArchive.JsonLineOptions);
            if (source is not null)
            {
                sources[source.SourceId] = source;
            }
        }

        return sources;
    }

    private static Memory ToMemory(
        KnowledgePackMountRecord mount,
        KnowledgePackManifest manifest,
        KnowledgePackMemoryRecord record,
        KnowledgePackSourceRecord? source)
    {
        var metadata = JsonSerializer.SerializeToDocument(new
        {
            source = "knowledge_pack",
            pack_id = manifest.PackId,
            pack_version = manifest.Version,
            pack_name = manifest.Name,
            pack_type = manifest.PackType,
            record_id = record.RecordId,
            source_id = record.SourceId,
            source_path = record.SourcePath,
            source_title = source?.Title,
            source_checksum = source?.Checksum,
            source_section_id = record.SourceSectionId,
            confidence = record.Confidence,
            kind = record.Kind,
            mounted_at = mount.MountedAt
        }, KnowledgePackArchive.JsonOptions);

        var id = StableGuid($"{manifest.PackId}:{manifest.Version}:{record.RecordId}");
        return new Memory(
            new MemoryId(id),
            new EventId(StableGuid($"event:{id}")),
            mount.Namespace,
            record.Content,
            metadata,
            manifest.Publisher,
            mount.MountedAt,
            mount.MountedAt);
    }

    private static double Score(string query, KnowledgePackMemoryRecord record)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return 0;
        }

        var haystack = $"{record.Content}\n{record.SourcePath}\n{record.Kind}";
        var matched = tokens.Count(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
        return matched == 0 ? 0 : PackScoreBase + (double)matched / tokens.Length;
    }

    private static Guid StableGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes[..16]);
    }
}
