namespace Banyan.Core;

public interface IEmbedder
{
    int Dimensions { get; }
    string ModelId { get; }
    ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default);
    ValueTask<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>
    /// Embed a search query. Default: same as <see cref="EmbedAsync"/>. Embedders trained with task
    /// prefixes (e.g. e5: "query: " vs "passage: ") override this to use the query side.
    /// </summary>
    ValueTask<float[]> EmbedQueryAsync(string text, CancellationToken ct = default) => EmbedAsync(text, ct);
}
