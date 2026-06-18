// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;

namespace Banyan.Embedders;

/// <summary>
/// Offline, zero-dependency embedder using the FastText-style subword hashing trick:
/// build a unit vector by accumulating buckets indexed by FNV-1a hashes of character
/// n-grams (3..5). Deterministic and language-agnostic — not "semantic" the way an
/// LLM-trained model is, but materially better than BM25 at matching morphological
/// variants ("deadline" ↔ "deadlines", 中文部分匹配, code/path tokens).
/// Replace with <c>OnnxEmbedder(multilingual-e5-small)</c> when a real model is available.
/// </summary>
public sealed class HashingEmbedder : IEmbedder
{
    public int    Dimensions => 384;
    public string ModelId    => "hashing-ngram-v1";

    private const int  MinGram = 3;
    private const int  MaxGram = 5;

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => ValueTask.FromResult(Embed(text));

    public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) result[i] = Embed(texts[i]);
        return ValueTask.FromResult(result);
    }

    /// <summary>Deterministic 384-dim unit vector for a string.</summary>
    public float[] Embed(string text)
    {
        var v = new float[Dimensions];
        if (string.IsNullOrEmpty(text)) return v;

        // Lowercase + strip surrounding whitespace; treat the rest as a stream of code units.
        var s = text.ToLowerInvariant();
        var span = s.AsSpan();

        // Whitespace-pad each token so n-grams capture word boundaries (FastText convention).
        // We do this on the fly by treating runs of non-whitespace as tokens.
        int i = 0;
        while (i < span.Length)
        {
            // skip whitespace
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            int start = i;
            while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
            if (start == i) break;

            // wrap token with single underscore on each side: "_<token>_"
            // We don't actually allocate; we just shift the n-gram window by treating
            // boundary chars as the constant '_'.
            EmitNgrams(span.Slice(start, i - start), v);
        }

        // L2 normalise so cosine similarity reduces to dot product.
        double norm = 0;
        for (int k = 0; k < v.Length; k++) norm += v[k] * v[k];
        if (norm > 0)
        {
            float inv = (float)(1.0 / Math.Sqrt(norm));
            for (int k = 0; k < v.Length; k++) v[k] *= inv;
        }
        return v;
    }

    private void EmitNgrams(ReadOnlySpan<char> token, float[] v)
    {
        // Padded length is token.Length + 2 (one '_' on each side).
        int padded = token.Length + 2;

        for (int n = MinGram; n <= MaxGram; n++)
        {
            if (padded < n) continue;
            for (int s = 0; s + n <= padded; s++)
            {
                // FNV-1a over the (start..start+n) window.
                ulong hash = 0xcbf29ce484222325UL;
                for (int k = 0; k < n; k++)
                {
                    int idx = s + k;
                    char c = (idx == 0 || idx == padded - 1) ? '_' : token[idx - 1];
                    hash ^= (byte)c;
                    hash *= 0x100000001b3UL;
                    hash ^= (byte)(c >> 8);  // fold high byte for non-ASCII (中文等)
                    hash *= 0x100000001b3UL;
                }
                int bucket = (int)(hash % (ulong)Dimensions);
                v[bucket] += 1f;
            }
        }
    }
}
