using Banyan.Embedders;
using Xunit;

namespace Banyan.Lite.Tests;

public sealed class HashingEmbedderTests
{
    private readonly HashingEmbedder _e = new();

    [Fact]
    public void Dimensions_Is_384() => Assert.Equal(384, _e.Dimensions);

    [Fact]
    public void Embed_IsDeterministic()
    {
        var a = _e.Embed("hello world");
        var b = _e.Embed("hello world");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Embed_ReturnsUnitVector()
    {
        var v = _e.Embed("the quick brown fox jumps over the lazy dog");
        var norm = Math.Sqrt(v.Sum(x => x * x));
        Assert.InRange(norm, 0.999, 1.001);
    }

    [Fact]
    public void Embed_EmptyString_ReturnsZeroVector()
    {
        var v = _e.Embed("");
        Assert.All(v, x => Assert.Equal(0f, x));
    }

    [Fact]
    public void Embed_MorphologicalVariants_AreCloser_ThanUnrelated()
    {
        var deadline  = _e.Embed("project deadline");
        var deadlines = _e.Embed("project deadlines");
        var unrelated = _e.Embed("zebra crossing afternoon");

        var simRelated   = Dot(deadline, deadlines);
        var simUnrelated = Dot(deadline, unrelated);

        Assert.True(simRelated > simUnrelated,
            $"morphological variants ({simRelated:F3}) should outscore unrelated ({simUnrelated:F3})");
    }

    [Fact]
    public async Task EmbedBatchAsync_IsBatchOfEmbedAsync()
    {
        var inputs = new[] { "alpha", "beta", "gamma" };
        var batch  = await _e.EmbedBatchAsync(inputs);
        Assert.Equal(3, batch.Length);
        for (int i = 0; i < inputs.Length; i++)
        {
            var single = await _e.EmbedAsync(inputs[i]);
            Assert.Equal(single, batch[i]);
        }
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
