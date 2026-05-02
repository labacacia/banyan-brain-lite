using Banyan.Core;

namespace Banyan.Embedders;

/// <summary>
/// Resolves an <see cref="IEmbedder"/> from environment variables, falling back to
/// <see cref="HashingEmbedder"/> when ONNX assets are unavailable.
///
/// Recognised env vars:
///   <c>BANYAN_EMBEDDER</c>           — <c>onnx</c> | <c>hashing</c> | <c>auto</c> (default)
///   <c>BANYAN_EMBEDDER_MODEL</c>     — path to the .onnx model file
///   <c>BANYAN_EMBEDDER_TOKENIZER</c> — path to the SentencePiece .model file
/// </summary>
public static class EmbedderFactory
{
    public const string EnvKind  = "BANYAN_EMBEDDER";
    public const string EnvModel = "BANYAN_EMBEDDER_MODEL";
    public const string EnvVocab = "BANYAN_EMBEDDER_VOCAB";

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
