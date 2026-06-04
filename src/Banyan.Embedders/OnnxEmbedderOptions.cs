namespace Banyan.Embedders;

/// <summary>Options for <see cref="OnnxEmbedder"/>. Paths support a leading <c>~</c>.</summary>
public sealed class OnnxEmbedderOptions
{
    /// <summary>Path to the ONNX model file. Default targets <c>all-MiniLM-L6-v2</c> (Xenova INT8, ~23MB).</summary>
    public string ModelPath { get; set; } = "~/.banyan/embedder/model.onnx";

    /// <summary>Path to the BERT WordPiece vocabulary.</summary>
    public string VocabPath { get; set; } = "~/.banyan/embedder/vocab.txt";

    /// <summary>Embedding dimensionality the ONNX model emits (bge-small / MiniLM-L6: 384).</summary>
    public int Dimensions { get; set; } = 384;

    /// <summary>Hard cap on sequence length. BGE / MiniLM trained at 512.</summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>Logical model id stored in the embeddings table; helps detect mixed-model corpora.</summary>
    public string ModelId { get; set; } = "all-MiniLM-L6-v2.onnx.q8";

    /// <summary>Optional retrieval-time query prefix. MiniLM embeds queries and passages directly.</summary>
    public string QueryPrefix { get; set; } = "";
}
