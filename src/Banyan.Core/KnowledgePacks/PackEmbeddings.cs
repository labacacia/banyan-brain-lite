// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Banyan.Core.KnowledgePacks;

/// <summary>
/// In-pack embedding store for <c>.banyanpack</c> v2 (KB-4). One binary blob keyed
/// by record id, so a mounted pack can be searched semantically without re-embedding.
/// Layout (little-endian): <c>[int32 count][int32 dim]</c> then <c>count ×
/// ([int32 idLen][idBytes utf8][dim × float32])</c>. All vectors share <c>dim</c>.
/// </summary>
public static class PackEmbeddings
{
    public const string Path = "embeddings/vectors.bin";

    public static byte[] Serialize(IReadOnlyDictionary<string, float[]> vectors)
    {
        var dim = vectors.Count == 0 ? 0 : vectors.Values.First().Length;
        foreach (var v in vectors.Values)
            if (v.Length != dim)
                throw new ArgumentException("all pack embeddings must share the same dimension.");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true); // BinaryWriter is little-endian
        w.Write(vectors.Count);
        w.Write(dim);
        foreach (var (id, vec) in vectors)
        {
            var idBytes = Encoding.UTF8.GetBytes(id);
            w.Write(idBytes.Length);
            w.Write(idBytes);
            for (var j = 0; j < dim; j++) w.Write(vec[j]);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static IReadOnlyDictionary<string, float[]> Deserialize(byte[] bytes)
    {
        var result = new Dictionary<string, float[]>(StringComparer.Ordinal);
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8);
        var count = r.ReadInt32();
        var dim = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var idLen = r.ReadInt32();
            var id = Encoding.UTF8.GetString(r.ReadBytes(idLen));
            var vec = new float[dim];
            for (var j = 0; j < dim; j++) vec[j] = r.ReadSingle();
            result[id] = vec;
        }
        return result;
    }
}

/// <summary>Vector helpers for in-pack semantic search (KB-4).</summary>
public static class VectorMath
{
    /// <summary>Cosine similarity in [-1, 1]; 0 when either vector is zero or lengths differ.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
