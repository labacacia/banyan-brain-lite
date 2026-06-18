using Banyan.Embedders;
using Xunit;

namespace Banyan.Lite.Tests;

/// <summary>
/// Integration tests against a real ONNX model. They auto-skip when the model isn't downloaded.
/// To enable, run <c>banyan embedder download</c> (or set BANYAN_EMBEDDER_MODEL / _VOCAB).
/// </summary>
public sealed class OnnxEmbedderTests
{
    private static OnnxEmbedderOptions ResolvedOptions()
    {
        var opts = new OnnxEmbedderOptions();
        if (Environment.GetEnvironmentVariable("BANYAN_EMBEDDER_MODEL") is { Length: > 0 } m) opts.ModelPath = m;
        if (Environment.GetEnvironmentVariable("BANYAN_EMBEDDER_VOCAB") is { Length: > 0 } v) opts.VocabPath = v;
        return opts;
    }

    private static bool ModelAvailable(out OnnxEmbedderOptions opts)
    {
        opts = ResolvedOptions();
        return File.Exists(EmbedderPaths.ExpandHome(opts.ModelPath))
            && File.Exists(EmbedderPaths.ExpandHome(opts.VocabPath));
    }

    private static double Cosine(float[] a, float[] b)
    {
        // vectors come from OnnxEmbedder L2-normalised, so dot product = cosine similarity.
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    [Fact]
    public void Open_ThrowsWhenModelMissing()
    {
        var opts = new OnnxEmbedderOptions
        {
            ModelPath = "/tmp/nonexistent/model.onnx",
            VocabPath = "/tmp/nonexistent/vocab.txt",
        };
        Assert.Throws<FileNotFoundException>(() => OnnxEmbedder.Open(opts));
    }

    [Fact]
    public async Task Embed_ReturnsUnitVector_OfExpectedDim()
    {
        if (!ModelAvailable(out var opts)) return;
        using var emb = OnnxEmbedder.Open(opts);

        var v = await emb.EmbedAsync("the quick brown fox");
        Assert.Equal(384, v.Length);

        double norm = Math.Sqrt(v.Sum(x => x * x));
        Assert.InRange(norm, 0.99, 1.01);
    }

    [Fact]
    public async Task Embed_IsDeterministic()
    {
        if (!ModelAvailable(out var opts)) return;
        using var emb = OnnxEmbedder.Open(opts);

        var a = await emb.EmbedAsync("hello banyan");
        var b = await emb.EmbedAsync("hello banyan");
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Embed_SemanticallyRelated_ScoresHigherThanUnrelated()
    {
        if (!ModelAvailable(out var opts)) return;
        using var emb = OnnxEmbedder.Open(opts);

        var deadline   = await emb.EmbedAsync("project deadline next Friday");
        var dueDate    = await emb.EmbedAsync("the due date is next Friday for the milestone");
        var unrelated1 = await emb.EmbedAsync("zebra crossing in the afternoon");
        var unrelated2 = await emb.EmbedAsync("the cat sat on the mat purring loudly");

        var simRelated   = Cosine(deadline, dueDate);
        var simUnrelated = Math.Max(Cosine(deadline, unrelated1), Cosine(deadline, unrelated2));

        Assert.True(simRelated > simUnrelated,
            $"semantically related should outscore unrelated: {simRelated:F3} vs {simUnrelated:F3}");
    }

    [Fact]
    public async Task EmbedQuery_AddsRetrievalPrefix_VsPassage()
    {
        if (!ModelAvailable(out var opts)) return;
        using var emb = OnnxEmbedder.Open(opts);

        // Same surface text, but as query vs passage. BGE is trained so the query side
        // is shifted by a known prefix; vectors should differ.
        var asPassage = await emb.EmbedAsync("project deadline");
        var asQuery   = await emb.EmbedQueryAsync("project deadline");
        Assert.NotEqual(asPassage, asQuery);
        // …but they should still align meaningfully (cosine well above random).
        Assert.True(Cosine(asPassage, asQuery) > 0.5);
    }

    [Fact]
    public async Task Embed_HandlesChineseAndEnglishMix()
    {
        if (!ModelAvailable(out var opts)) return;
        using var emb = OnnxEmbedder.Open(opts);

        var en = await emb.EmbedAsync("project deadline next Monday");
        var zh = await emb.EmbedAsync("项目截止日期下周一");
        var unrelated = await emb.EmbedAsync("zebra crossing afternoon");

        var simCross   = Cosine(en, zh);
        var simRandom  = Cosine(en, unrelated);
        Assert.True(simCross > simRandom,
            $"cross-language match should outscore random: {simCross:F3} vs {simRandom:F3}");
    }
}
