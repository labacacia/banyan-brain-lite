using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Banyan.Core;
using Microsoft.Data.Sqlite;

namespace Banyan.Lite;

/// <summary>
/// Single-connection SQLite-backed memory store.
/// When constructed with an <see cref="IEmbedder"/>, writes also produce an embedding
/// row and <see cref="SearchAsync"/> supports <see cref="SearchMode.Vector"/> and
/// <see cref="SearchMode.Hybrid"/> (RRF fusion). Without an embedder, only
/// <see cref="SearchMode.Lexical"/> (BM25) is meaningful.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly SqliteConnection _conn;
    private readonly IEmbedder?       _embedder;
    private readonly bool             _vecEnabled;

    /// <summary>RRF rank-fusion constant (Cormack et al, 2009): 60 in the original paper.</summary>
    private const int RrfK = 60;

    private SqliteMemoryStore(SqliteConnection conn, IEmbedder? embedder, bool vecEnabled)
    {
        _conn = conn;
        _embedder = embedder;
        _vecEnabled = vecEnabled;
    }

    public static async Task<SqliteMemoryStore> OpenAsync(
        string connectionString, IEmbedder? embedder = null,
        string? sqliteVecLibPath = null, CancellationToken ct = default)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        // Load the sqlite-vec extension before running migrations so we can also create
        // the vec0 virtual table when its codec is available.
        bool vecEnabled = embedder is not null && SqliteVecLoader.TryLoad(conn, sqliteVecLibPath);
        await Migrations.ApplyAsync(conn, ct);
        if (vecEnabled) await EnsureVecIndexAsync(conn, embedder!.Dimensions, ct);
        return new SqliteMemoryStore(conn, embedder, vecEnabled);
    }

    public static Task<SqliteMemoryStore> OpenInMemoryAsync(
        IEmbedder? embedder = null, string? sqliteVecLibPath = null, CancellationToken ct = default)
        => OpenAsync("Data Source=:memory:", embedder, sqliteVecLibPath, ct);

    /// <summary>True when an embedder is configured; required for vector/hybrid search.</summary>
    public bool HasEmbedder => _embedder is not null;

    /// <summary>True when sqlite-vec was loaded and the <c>embeddings_vec</c> ANN index is in use.</summary>
    public bool VecEnabled => _vecEnabled;

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<MemoryId> WriteAsync(WriteRequest req, CancellationToken ct = default)
    {
        var memoryId = MemoryId.New();
        var eventId  = EventId.New();
        var now      = DateTimeOffset.UtcNow.ToString("O");
        var meta     = req.Metadata?.RootElement.GetRawText();

        float[]? vec = _embedder is not null ? await _embedder.EmbedAsync(req.Content, ct) : null;

        using var tx = _conn.BeginTransaction();

        Exec(tx, """
            INSERT INTO memory_events
                (event_id, memory_id, type, content, metadata, agent_nid, namespace, occurred_at)
            VALUES (@eid, @mid, 0, @content, @meta, @agent, @ns, @now)
            """,
            ("@eid",     eventId.ToString()),
            ("@mid",     memoryId.ToString()),
            ("@content", req.Content),
            ("@meta",    (object?)meta  ?? DBNull.Value),
            ("@agent",   (object?)req.AgentNid ?? DBNull.Value),
            ("@ns",      req.Namespace),
            ("@now",     now));

        Exec(tx, """
            INSERT INTO memories_current
                (memory_id, event_id, namespace, content, metadata, agent_nid, created_at, updated_at)
            VALUES (@mid, @eid, @ns, @content, @meta, @agent, @now, @now)
            ON CONFLICT (memory_id) DO NOTHING
            """,
            ("@mid",     memoryId.ToString()),
            ("@eid",     eventId.ToString()),
            ("@ns",      req.Namespace),
            ("@content", req.Content),
            ("@meta",    (object?)meta  ?? DBNull.Value),
            ("@agent",   (object?)req.AgentNid ?? DBNull.Value),
            ("@now",     now));

        Exec(tx, """
            INSERT INTO memories_fts (memory_id, namespace, content)
            VALUES (@mid, @ns, @content)
            """,
            ("@mid",     memoryId.ToString()),
            ("@ns",      req.Namespace),
            ("@content", req.Content));

        if (vec is not null && _embedder is not null)
        {
            var bytes = FloatsToBytes(vec);
            Exec(tx, """
                INSERT INTO embeddings (memory_id, namespace, model_id, dim, vector, updated_at)
                VALUES (@mid, @ns, @model, @dim, @vec, @now)
                """,
                ("@mid",   memoryId.ToString()),
                ("@ns",    req.Namespace),
                ("@model", _embedder.ModelId),
                ("@dim",   _embedder.Dimensions),
                ("@vec",   bytes),
                ("@now",   now));

            if (_vecEnabled)
                Exec(tx, "INSERT INTO embeddings_vec(memory_id, embedding) VALUES (@mid, @vec)",
                    ("@mid", memoryId.ToString()), ("@vec", bytes));
        }

        tx.Commit();
        return memoryId;
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<EventId> UpdateAsync(MemoryId id, UpdateRequest req, CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Memory {id} not found.");

        var eventId = EventId.New();
        var now     = DateTimeOffset.UtcNow.ToString("O");
        var meta    = req.Metadata?.RootElement.GetRawText();
        var ns      = current.Namespace;

        float[]? vec = _embedder is not null ? await _embedder.EmbedAsync(req.Content, ct) : null;

        using var tx = _conn.BeginTransaction();

        Exec(tx, """
            INSERT INTO memory_events
                (event_id, memory_id, type, content, metadata, agent_nid, namespace, occurred_at)
            VALUES (@eid, @mid, 1, @content, @meta, @agent, @ns, @now)
            """,
            ("@eid",     eventId.ToString()),
            ("@mid",     id.ToString()),
            ("@content", req.Content),
            ("@meta",    (object?)meta  ?? DBNull.Value),
            ("@agent",   (object?)req.AgentNid ?? DBNull.Value),
            ("@ns",      ns),
            ("@now",     now));

        Exec(tx, """
            UPDATE memories_current
            SET event_id = @eid, content = @content, metadata = @meta, agent_nid = @agent, updated_at = @now
            WHERE memory_id = @mid
            """,
            ("@mid",     id.ToString()),
            ("@eid",     eventId.ToString()),
            ("@content", req.Content),
            ("@meta",    (object?)meta  ?? DBNull.Value),
            ("@agent",   (object?)req.AgentNid ?? DBNull.Value),
            ("@now",     now));

        // FTS5 has no UPDATE-by-key — delete + insert.
        Exec(tx, "DELETE FROM memories_fts WHERE memory_id = @mid", ("@mid", id.ToString()));
        Exec(tx, """
            INSERT INTO memories_fts (memory_id, namespace, content) VALUES (@mid, @ns, @content)
            """,
            ("@mid",     id.ToString()),
            ("@ns",      ns),
            ("@content", req.Content));

        if (vec is not null && _embedder is not null)
        {
            var bytes = FloatsToBytes(vec);
            Exec(tx, """
                INSERT INTO embeddings (memory_id, namespace, model_id, dim, vector, updated_at)
                VALUES (@mid, @ns, @model, @dim, @vec, @now)
                ON CONFLICT (memory_id) DO UPDATE SET
                    namespace  = excluded.namespace,
                    model_id   = excluded.model_id,
                    dim        = excluded.dim,
                    vector     = excluded.vector,
                    updated_at = excluded.updated_at
                """,
                ("@mid",   id.ToString()),
                ("@ns",    ns),
                ("@model", _embedder.ModelId),
                ("@dim",   _embedder.Dimensions),
                ("@vec",   bytes),
                ("@now",   now));

            if (_vecEnabled)
            {
                // vec0 has no UPSERT — delete-then-insert per row.
                Exec(tx, "DELETE FROM embeddings_vec WHERE memory_id = @mid", ("@mid", id.ToString()));
                Exec(tx, "INSERT INTO embeddings_vec(memory_id, embedding) VALUES (@mid, @vec)",
                    ("@mid", id.ToString()), ("@vec", bytes));
            }
        }

        tx.Commit();
        return eventId;
    }

    // ── Forget ────────────────────────────────────────────────────────────────

    public async Task<EventId> ForgetAsync(MemoryId id, string? reason = null, CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Memory {id} not found.");

        var eventId = EventId.New();
        var now     = DateTimeOffset.UtcNow.ToString("O");
        var ns      = current.Namespace;

        // Tombstone metadata carries the reason (auditable in trace).
        var tombstoneMeta = reason is null ? null : JsonSerializer.Serialize(new { reason });

        using var tx = _conn.BeginTransaction();

        Exec(tx, """
            INSERT INTO memory_events
                (event_id, memory_id, type, content, metadata, agent_nid, namespace, occurred_at)
            VALUES (@eid, @mid, 2, NULL, @meta, NULL, @ns, @now)
            """,
            ("@eid",  eventId.ToString()),
            ("@mid",  id.ToString()),
            ("@meta", (object?)tombstoneMeta ?? DBNull.Value),
            ("@ns",   ns),
            ("@now",  now));

        // Drop the searchable presence; the event log still has it for trace.
        Exec(tx, "DELETE FROM memories_current WHERE memory_id = @mid", ("@mid", id.ToString()));
        Exec(tx, "DELETE FROM memories_fts     WHERE memory_id = @mid", ("@mid", id.ToString()));
        Exec(tx, "DELETE FROM embeddings       WHERE memory_id = @mid", ("@mid", id.ToString()));
        if (_vecEnabled)
            Exec(tx, "DELETE FROM embeddings_vec WHERE memory_id = @mid", ("@mid", id.ToString()));

        tx.Commit();
        return eventId;
    }

    // ── Get / Recall ──────────────────────────────────────────────────────────

    public async Task<Memory?> GetAsync(MemoryId id, CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT memory_id, event_id, namespace, content, metadata, agent_nid, created_at, updated_at
            FROM   memories_current
            WHERE  memory_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());

        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadMemory(r) : null;
    }

    public async Task<IReadOnlyList<Memory>> RecallAsync(
        IEnumerable<MemoryId> ids, CancellationToken ct = default)
    {
        var result = new List<Memory>();
        foreach (var id in ids)
        {
            var m = await GetAsync(id, ct);
            if (m is not null) result.Add(m);
        }
        return result;
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public IAsyncEnumerable<SearchHit> SearchAsync(
        SearchQuery query, CancellationToken ct = default)
        => query.Mode switch
        {
            SearchMode.Lexical => LexicalSearchAsync(query, ct),
            SearchMode.Vector  => VectorSearchAsync (query, ct),
            SearchMode.Hybrid  => HybridSearchAsync (query, ct),
            _                  => HybridSearchAsync (query, ct),
        };

    /// <summary>BM25 over FTS5. Returns hits ordered by descending score.</summary>
    public async IAsyncEnumerable<SearchHit> LexicalSearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text)) yield break;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT mc.memory_id, mc.event_id, mc.namespace, mc.content, mc.metadata,
                   mc.agent_nid, mc.created_at, mc.updated_at,
                   -bm25(memories_fts) AS score
            FROM   memories_fts
            JOIN   memories_current mc ON mc.memory_id = memories_fts.memory_id
            WHERE  memories_fts MATCH @q
            AND   (@ns IS NULL OR memories_fts.namespace = @ns)
            ORDER BY score DESC
            LIMIT @k
            """;
        cmd.Parameters.AddWithValue("@q",  BuildFtsQuery(query.Text));
        cmd.Parameters.AddWithValue("@ns", (object?)query.Namespace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@k",  query.K);

        using var r = await cmd.ExecuteReaderAsync(ct);
        int rank = 0;
        while (await r.ReadAsync(ct))
        {
            rank++;
            yield return new SearchHit(ReadMemory(r), r.GetDouble(8), null, rank);
        }
    }

    /// <summary>
    /// Top-K cosine similarity. Uses sqlite-vec's vec0 ANN when available, otherwise falls back
    /// to a full-table linear scan over the <c>embeddings</c> BLOB column. Both vectors are
    /// L2-normalised, so cosine and L2² are monotonically related — vec0's <c>distance</c> = L2²,
    /// which we map back to cosine for callers.
    /// </summary>
    public async IAsyncEnumerable<SearchHit> VectorSearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_embedder is null || string.IsNullOrWhiteSpace(query.Text)) yield break;

        var qvec  = await _embedder.EmbedQueryAsync(query.Text, ct);
        var qbytes = FloatsToBytes(qvec);

        var top = _vecEnabled
            ? await VecAnnTopKAsync(qbytes, query, ct)
            : LinearTopK(qvec, query);

        int rank = 0;
        foreach (var (memId, score) in top)
        {
            rank++;
            var m = await GetAsync(new MemoryId(Guid.Parse(memId)), ct);
            if (m is null) continue;
            yield return new SearchHit(m, score, rank, null);
        }
    }

    private async Task<List<(string memId, double score)>> VecAnnTopKAsync(byte[] qbytes, SearchQuery query, CancellationToken ct)
    {
        // vec0 doesn't support filtering on a non-indexed column inside MATCH. Instead we
        // over-fetch from the ANN, then post-filter by namespace via a join on `embeddings`.
        int kRequested = Math.Max(query.K, 1);
        int kFetch     = query.Namespace is null ? kRequested : Math.Min(kRequested * 8, 1024);

        var ranked = new List<(string memId, double score)>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.memory_id, v.distance, e.namespace
            FROM   embeddings_vec v
            JOIN   embeddings     e ON e.memory_id = v.memory_id
            WHERE  v.embedding MATCH @q AND k = @k
            AND   (@ns IS NULL OR e.namespace = @ns)
            ORDER BY v.distance
            LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("@q",   qbytes);
        cmd.Parameters.AddWithValue("@k",   kFetch);
        cmd.Parameters.AddWithValue("@ns",  (object?)query.Namespace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lim", kRequested);

        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // sqlite-vec default distance metric is L2-squared; for unit-length vectors
            // cosine = 1 - L2²/2.  Pick whichever is more intuitive — we expose cosine.
            var distance = r.GetDouble(1);
            var cosine   = 1.0 - distance / 2.0;
            ranked.Add((r.GetString(0), cosine));
        }
        return ranked;
    }

    private List<(string memId, double score)> LinearTopK(float[] qvec, SearchQuery query)
    {
        var ranked = new List<(string, double)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT memory_id, vector FROM embeddings
            WHERE @ns IS NULL OR namespace = @ns
            """;
        cmd.Parameters.AddWithValue("@ns", (object?)query.Namespace ?? DBNull.Value);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var bytes = (byte[])r["vector"];
            var v = BytesToFloats(bytes);
            if (v.Length != qvec.Length) continue;
            ranked.Add((r.GetString(0), Dot(qvec, v)));
        }
        return ranked.OrderByDescending(t => t.Item2).Take(query.K).ToList();
    }

    /// <summary>BM25 ⊕ ANN with Reciprocal Rank Fusion. Falls back to lexical when no embedder.</summary>
    public async IAsyncEnumerable<SearchHit> HybridSearchAsync(
        SearchQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_embedder is null)
        {
            await foreach (var h in LexicalSearchAsync(query, ct)) yield return h;
            yield break;
        }

        // Pull a deeper pool from each ranker than we'll return, so RRF has signal.
        int pool = Math.Max(query.K * 4, 50);
        var deepQuery = new SearchQuery(query.Text, K: pool, query.Namespace, SearchMode.Lexical);

        var lexHits = new Dictionary<string, (int rank, SearchHit hit)>();
        var lexRank = 0;
        await foreach (var h in LexicalSearchAsync(deepQuery, ct))
        {
            lexRank++;
            lexHits[h.Memory.Id.ToString()] = (lexRank, h);
        }

        var vecQuery = new SearchQuery(query.Text, K: pool, query.Namespace, SearchMode.Vector);
        var vecHits = new Dictionary<string, (int rank, SearchHit hit)>();
        var vecRank = 0;
        await foreach (var h in VectorSearchAsync(vecQuery, ct))
        {
            vecRank++;
            vecHits[h.Memory.Id.ToString()] = (vecRank, h);
        }

        // Fuse: score = 1/(K+lexRank) + 1/(K+vecRank), missing terms = 0.
        var allIds = new HashSet<string>(lexHits.Keys);
        allIds.UnionWith(vecHits.Keys);

        var fused = new List<(SearchHit baseHit, double score, int? lex, int? vec)>();
        foreach (var id in allIds)
        {
            int? lr = lexHits.TryGetValue(id, out var l) ? l.rank : (int?)null;
            int? vr = vecHits.TryGetValue(id, out var v) ? v.rank : (int?)null;

            double score = 0;
            if (lr is { } x) score += 1.0 / (RrfK + x);
            if (vr is { } y) score += 1.0 / (RrfK + y);

            var baseHit = (vecHits.TryGetValue(id, out var vh) ? vh.hit
                          : lexHits[id].hit);
            fused.Add((baseHit, score, lr, vr));
        }

        foreach (var (baseHit, score, lr, vr) in fused.OrderByDescending(t => t.score).Take(query.K))
            yield return new SearchHit(baseHit.Memory, score, vr, lr);
    }

    // ── Trace ─────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<MemoryEvent> TraceAsync(
        MemoryId id,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, memory_id, type, content, metadata, agent_nid, namespace, occurred_at
            FROM   memory_events
            WHERE  memory_id = @id
            ORDER BY occurred_at, event_id
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString());

        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            yield return ReadEvent(r);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        _conn.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Memory ReadMemory(SqliteDataReader r) => new(
        Id:            new MemoryId(Guid.Parse(r.GetString(0))),
        LatestEventId: new EventId(Guid.Parse(r.GetString(1))),
        Namespace:     r.GetString(2),
        Content:       r.GetString(3),
        Metadata:      r.IsDBNull(4) ? null : JsonDocument.Parse(r.GetString(4)),
        AgentNid:      r.IsDBNull(5) ? null : r.GetString(5),
        CreatedAt:     DateTimeOffset.Parse(r.GetString(6)),
        UpdatedAt:     DateTimeOffset.Parse(r.GetString(7))
    );

    private static MemoryEvent ReadEvent(SqliteDataReader r) => new(
        Id:         new EventId(Guid.Parse(r.GetString(0))),
        MemoryId:   new MemoryId(Guid.Parse(r.GetString(1))),
        Type:       (MemoryEventType)r.GetInt32(2),
        Content:    r.IsDBNull(3) ? null : r.GetString(3),
        Metadata:   r.IsDBNull(4) ? null : JsonDocument.Parse(r.GetString(4)),
        AgentNid:   r.IsDBNull(5) ? null : r.GetString(5),
        Namespace:  r.GetString(6),
        OccurredAt: DateTimeOffset.Parse(r.GetString(7))
    );

    private static string BuildFtsQuery(string text)
    {
        var tokens = text.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0
            ? "\"\""
            : string.Join(" ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\"*"));
    }

    private static byte[] FloatsToBytes(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        for (int i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), v[i]);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        for (int i = 0; i < v.Length; i++)
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
        return v;
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>
    /// Create the <c>embeddings_vec</c> vec0 virtual table if it doesn't exist and backfill from <c>embeddings</c>.
    /// vec0 indexes are an ANN sidecar; the source-of-truth is still the <c>embeddings</c> BLOB column.
    /// </summary>
    private static async Task EnsureVecIndexAsync(SqliteConnection conn, int dim, CancellationToken ct)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE VIRTUAL TABLE IF NOT EXISTS embeddings_vec
                USING vec0(memory_id TEXT PRIMARY KEY, embedding float[{dim}])
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Backfill: any embeddings row not yet in the vec index gets inserted.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT e.memory_id, e.vector
                FROM   embeddings e
                LEFT   JOIN embeddings_vec v ON v.memory_id = e.memory_id
                WHERE  v.memory_id IS NULL
                """;
            using var r = await cmd.ExecuteReaderAsync(ct);
            var pending = new List<(string id, byte[] vec)>();
            while (await r.ReadAsync(ct)) pending.Add((r.GetString(0), (byte[])r["vector"]));
            r.Close();

            foreach (var (id, vec) in pending)
            {
                await using var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO embeddings_vec(memory_id, embedding) VALUES (@id, @v)";
                ins.Parameters.AddWithValue("@id", id);
                ins.Parameters.AddWithValue("@v",  vec);
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static void Exec(
        SqliteTransaction tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }
}
