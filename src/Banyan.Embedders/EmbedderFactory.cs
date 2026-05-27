using Banyan.Core;

namespace Banyan.Embedders;

/// <summary>
/// Resolves an <see cref="IEmbedder"/> from environment variables, falling back to
/// <see cref="HashingEmbedder"/> when ONNX assets are unavailable.
///
/// Recognised env vars:
///   <c>BANYAN_EMBEDDER</c>           — <c>onnx</c> | <c>hashing</c> | <c>auto</c> (default)
///   <c>BANYAN_EMBEDDER_MODEL</c>     — path to the .onnx model file
///   <c>BANYAN_EMBEDDER_VOCAB</c>     — path to the BERT WordPiece vocab.txt file
///   <c>BANYAN_EMBEDDER_MODEL_ID</c>  — logical model id stored with vectors
///   <c>BANYAN_EMBEDDER_QUERY_PREFIX</c> — optional retrieval-query prefix
/// </summary>
public static class EmbedderFactory
{
    public const string EnvKind  = "BANYAN_EMBEDDER";
    public const string EnvModel = "BANYAN_EMBEDDER_MODEL";
    public const string EnvVocab = "BANYAN_EMBEDDER_VOCAB";
    public const string EnvModelId = "BANYAN_EMBEDDER_MODEL_ID";
    public const string EnvQueryPrefix = "BANYAN_EMBEDDER_QUERY_PREFIX";
    public const string EnvDimensions = "BANYAN_EMBEDDER_DIMENSIONS";
    public const string EnvMaxSequenceLength = "BANYAN_EMBEDDER_MAX_SEQUENCE_LENGTH";

    public static IEmbedder Create(TextWriter? log = null)
    {
        log ??= Console.Out;
        var kind = (Environment.GetEnvironmentVariable(EnvKind) ?? "auto").ToLowerInvariant();

        if (kind == "hashing")
        {
            log.WriteLine("[embedder] BANYAN_EMBEDDER=hashing → HashingEmbedder (offline n-gram).");
            return new HashingEmbedder();
        }

        if (kind is "onnx" or "auto")
        {
            var opts = new OnnxEmbedderOptions();
            if (Environment.GetEnvironmentVariable(EnvModel) is { Length: > 0 } m) opts.ModelPath = m;
            if (Environment.GetEnvironmentVariable(EnvVocab) is { Length: > 0 } v) opts.VocabPath = v;
            if (Environment.GetEnvironmentVariable(EnvModelId) is { Length: > 0 } id) opts.ModelId = id;
            if (Environment.GetEnvironmentVariable(EnvQueryPrefix) is { } prefix) opts.QueryPrefix = prefix;
            if (Environment.GetEnvironmentVariable(EnvDimensions) is { Length: > 0 } dims &&
                int.TryParse(dims, out var dim) && dim > 0)
                opts.Dimensions = dim;
            if (Environment.GetEnvironmentVariable(EnvMaxSequenceLength) is { Length: > 0 } maxSeq &&
                int.TryParse(maxSeq, out var max) && max > 0)
                opts.MaxSequenceLength = max;

            var modelOk = File.Exists(OnnxEmbedder.ExpandHome(opts.ModelPath));
            var tokOk   = File.Exists(OnnxEmbedder.ExpandHome(opts.VocabPath));

            if (modelOk && tokOk)
            {
                try
                {
                    var emb = OnnxEmbedder.Open(opts);
                    log.WriteLine($"[embedder] OnnxEmbedder ready: model={opts.ModelId}, dim={emb.Dimensions}");
                    return emb;
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[embedder] OnnxEmbedder failed to load ({ex.Message}); falling back to HashingEmbedder.");
                }
            }
            else if (kind == "onnx")
            {
                throw new FileNotFoundException(
                    $"BANYAN_EMBEDDER=onnx but model/vocab files are missing. " +
                    $"Run `banyan embedder download` or set {EnvModel}/{EnvVocab}.");
            }
            else
            {
                log.WriteLine($"[embedder] ONNX assets missing (model={modelOk}, vocab={tokOk}); using HashingEmbedder. Run `banyan embedder download` for semantic search.");
            }
        }

        return new HashingEmbedder();
    }
}
