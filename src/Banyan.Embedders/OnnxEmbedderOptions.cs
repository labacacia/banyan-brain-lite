namespace Banyan.Embedders;

/// <summary>Options for <see cref="OnnxEmbedder"/>. Paths support a leading <c>~</c>.</summary>
public sealed class OnnxEmbedderOptions
{
    /// <summary>Path to the ONNX model file. Default targets <c>bge-small-zh-v1.5</c> (Xenova INT8, ~24MB).</summary>
    public string ModelPath { get; set; } = "~/.banyan/embedder/model.onnx";

    /// <summary>Path to the BERT WordPiece vocabulary (vocab.txt, ~110KB for bge-small-zh).</summary>
    public string VocabPath { get; set; } = "~/.banyan/embedder/vocab.txt";

    /// <summary>Embedding dimensionality the ONNX model emits (bge-small / MiniLM-L6: 384).</summary>
    public int Dimensions { get; set; } = 384;

    /// <summary>Hard cap on sequence length. BGE / MiniLM trained at 512.</summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>Logical model id stored in the embeddings table; helps detect mixed-model corpora.</summary>
    public string ModelId { get; set; } = "bge-small-zh-v1.5.onnx.q8";

    /// <summary>BGE family expects a query prefix at retrieval time (passages embed raw).</summary>
    public string QueryPrefix { get; set; } = "为这个句子生成表示以用于检索相关文章：";
}
