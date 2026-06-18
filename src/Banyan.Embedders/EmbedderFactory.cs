using Banyan.Core;

namespace Banyan.Embedders;

/// <summary>
/// Resolves an <see cref="IEmbedder"/> from environment variables, falling back to
/// <see cref="HashingEmbedder"/> when ONNX assets — or the ONNX provider itself —
/// are unavailable.
///
/// The ONNX embedder lives in the optional <c>Banyan.Embedders.Onnx</c> package so
/// the core (and the global CLI tool) stays free of the ~130 MB Microsoft.ML.OnnxRuntime
/// native payload. A host that wants semantic embeddings references that package,
/// which registers itself into <see cref="OnnxProvider"/>; when it isn't present,
/// <c>auto</c> degrades to hashing and <c>onnx</c> throws a clear message.
///
/// Recognised env vars:
///   <c>BANYAN_EMBEDDER</c>           — <c>onnx</c> | <c>hashing</c> | <c>auto</c> (default)
///   <c>BANYAN_EMBEDDER_MODEL</c>     — path to the .onnx model file
///   <c>BANYAN_EMBEDDER_VOCAB</c>     — path to the WordPiece vocab.txt
/// </summary>
public static class EmbedderFactory
{
    public const string EnvKind  = "BANYAN_EMBEDDER";
    public const string EnvModel = "BANYAN_EMBEDDER_MODEL";
    public const string EnvVocab = "BANYAN_EMBEDDER_VOCAB";

    /// <summary>
    /// Pluggable ONNX provider. Set by <c>Banyan.Embedders.Onnx</c> when that package
    /// is referenced (via a module initializer / explicit registration). Receives the
    /// resolved options + a log writer and returns a ready <see cref="IEmbedder"/>.
    /// Null when the ONNX companion isn't present.
    /// </summary>
    public static Func<OnnxEmbedderOptions, TextWriter, IEmbedder>? OnnxProvider { get; set; }

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

            if (OnnxProvider is null)
            {
                if (kind == "onnx")
                    throw new InvalidOperationException(
                        "BANYAN_EMBEDDER=onnx but the ONNX provider is not registered. " +
                        "Reference the Banyan.Embedders.Onnx package (from NuGet) so semantic " +
                        "embeddings are available, or set BANYAN_EMBEDDER=hashing.");
                log.WriteLine("[embedder] ONNX provider not present; using HashingEmbedder. " +
                              "Add the Banyan.Embedders.Onnx package for semantic search.");
                return new HashingEmbedder();
            }

            var modelOk = File.Exists(EmbedderPaths.ExpandHome(opts.ModelPath));
            var tokOk   = File.Exists(EmbedderPaths.ExpandHome(opts.VocabPath));

            if (modelOk && tokOk)
            {
                try
                {
                    var emb = OnnxProvider(opts, log);
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
