using Banyan.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Banyan.Embedders;

/// <summary>
/// Real semantic embedder backed by an ONNX BERT-family model + WordPiece tokenizer.
/// Default config targets <c>bge-small-zh-v1.5</c> (Xenova INT8, 384-dim, multilingual-leaning Chinese).
/// Drop-in replacement: swap <see cref="OnnxEmbedderOptions.ModelPath"/> + <see cref="OnnxEmbedderOptions.VocabPath"/>
/// for <c>all-MiniLM-L6-v2</c> (English) or any other BERT WordPiece model with a vocab.txt.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    private readonly InferenceSession    _session;
    private readonly BertTokenizer       _tokenizer;
    private readonly OnnxEmbedderOptions _opts;
    private readonly bool                _hasTokenTypeIds;
    private readonly string              _outputName;
    private readonly bool                _outputIs2D;

    public int    Dimensions => _opts.Dimensions;
    public string ModelId    => _opts.ModelId;

    private OnnxEmbedder(InferenceSession session, BertTokenizer tokenizer, OnnxEmbedderOptions opts)
    {
        _session   = session;
        _tokenizer = tokenizer;
        _opts      = opts;

        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

        // Output name varies: last_hidden_state / sentence_embedding / embeddings.
        _outputName = _session.OutputMetadata.Keys.FirstOrDefault(k =>
            k is "last_hidden_state" or "sentence_embedding" or "embeddings")
            ?? _session.OutputMetadata.Keys.First();
        // 2D output (already pooled) vs 3D (token-level, needs mean pooling)
        var dims = _session.OutputMetadata[_outputName].Dimensions;
        _outputIs2D = dims.Length == 2;
    }

    /// <summary>Open an embedder by paths. Throws <see cref="FileNotFoundException"/> if either file is missing.</summary>
    public static OnnxEmbedder Open(OnnxEmbedderOptions opts)
    {
        var modelPath = ExpandHome(opts.ModelPath);
        var vocabPath = ExpandHome(opts.VocabPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found at {modelPath}. Run `banyan embedder download` first.", modelPath);
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"BERT vocab.txt not found at {vocabPath}. Run `banyan embedder download` first.", vocabPath);

        var sessionOpts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        var session     = new InferenceSession(modelPath, sessionOpts);
        var tokenizer   = BertTokenizer.Create(vocabPath, new BertOptions
        {
            // BGE / MiniLM are uncased; BertTokenizer handles [CLS]/[SEP]/[PAD] auto-add by default.
            LowerCaseBeforeTokenization = true,
            IndividuallyTokenizeCjk     = true,
            RemoveNonSpacingMarks       = true,
        });
        return new OnnxEmbedder(session, tokenizer, opts);
    }

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
        => ValueTask.FromResult(EmbedOne(text ?? ""));

    /// <summary>Variant that prepends the BGE retrieval-query prefix.</summary>
    public ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        => ValueTask.FromResult(EmbedOne(_opts.QueryPrefix + (text ?? "")));

    public ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++) result[i] = EmbedOne(texts[i] ?? "");
        return ValueTask.FromResult(result);
    }

    public void Dispose() => _session.Dispose();

    // ── Internals ─────────────────────────────────────────────────────────────

    private float[] EmbedOne(string text)
    {
        // BertTokenizer.EncodeToIds adds [CLS] at start and [SEP] at end automatically.
        var ids = _tokenizer.EncodeToIds(text, considerPreTokenization: true, considerNormalization: true);
        if (ids.Count > _opts.MaxSequenceLength)
            ids = ids.Take(_opts.MaxSequenceLength).ToList();

        int len = ids.Count;
        var inputIds      = new long[len];
        var attentionMask = new long[len];
        for (int i = 0; i < len; i++) { inputIds[i] = ids[i]; attentionMask[i] = 1; }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids",      new DenseTensor<long>(inputIds,      new[] { 1, len })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, new[] { 1, len })),
        };
        if (_hasTokenTypeIds)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[len], new[] { 1, len })));

        using var results = _session.Run(inputs);
        var hidden = results.First(r => r.Name == _outputName).AsTensor<float>();
        return _outputIs2D
            ? Normalise(hidden, _opts.Dimensions)
            : MeanPoolAndNormalise(hidden, attentionMask, _opts.Dimensions);
    }

    /// <summary>Mean-pool token embeddings over the attention mask, then L2-normalise. Standard for sentence-transformers / BGE.</summary>
    private static float[] MeanPoolAndNormalise(Tensor<float> hidden, long[] mask, int dim)
    {
        int seq = (int)hidden.Dimensions[1];
        var pooled = new float[dim];
        int kept = 0;
        for (int t = 0; t < seq; t++)
        {
            if (mask[t] == 0) continue;
            kept++;
            for (int d = 0; d < dim; d++) pooled[d] += hidden[0, t, d];
        }
        if (kept > 0) for (int d = 0; d < dim; d++) pooled[d] /= kept;
        return L2Normalise(pooled);
    }

    private static float[] Normalise(Tensor<float> pooled, int dim)
    {
        var v = new float[dim];
        for (int d = 0; d < dim; d++) v[d] = pooled[0, d];
        return L2Normalise(v);
    }

    private static float[] L2Normalise(float[] v)
    {
        double norm = 0;
        for (int d = 0; d < v.Length; d++) norm += v[d] * v[d];
        if (norm > 0)
        {
            float inv = (float)(1.0 / Math.Sqrt(norm));
            for (int d = 0; d < v.Length; d++) v[d] *= inv;
        }
        return v;
    }

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
