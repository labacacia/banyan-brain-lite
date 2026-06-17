// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Core.Tests;

public class PackEmbeddingsTests
{
    [Fact]
    public void Serialize_Deserialize_RoundTrip()
    {
        var vectors = new Dictionary<string, float[]>
        {
            ["r1"] = [0.1f, 0.2f, 0.3f],
            ["r2"] = [-1f, 0f, 0.5f],
        };
        var bytes = PackEmbeddings.Serialize(vectors);
        var back = PackEmbeddings.Deserialize(bytes);

        Assert.Equal(2, back.Count);
        Assert.Equal(vectors["r1"], back["r1"]);
        Assert.Equal(vectors["r2"], back["r2"]);
    }

    [Fact]
    public void Serialize_RejectsMismatchedDimensions()
    {
        var bad = new Dictionary<string, float[]> { ["a"] = [1f, 2f], ["b"] = [1f] };
        Assert.Throws<ArgumentException>(() => PackEmbeddings.Serialize(bad));
    }

    [Fact]
    public void Empty_RoundTrips()
    {
        var bytes = PackEmbeddings.Serialize(new Dictionary<string, float[]>());
        Assert.Empty(PackEmbeddings.Deserialize(bytes));
    }

    [Theory]
    [InlineData(new[] { 1f, 0f }, new[] { 1f, 0f }, 1.0)]
    [InlineData(new[] { 1f, 0f }, new[] { 0f, 1f }, 0.0)]
    [InlineData(new[] { 1f, 0f }, new[] { -1f, 0f }, -1.0)]
    public void Cosine_KnownValues(float[] a, float[] b, double expected)
        => Assert.Equal(expected, VectorMath.Cosine(a, b), 5);

    [Fact]
    public void Cosine_ZeroVector_IsZero()
        => Assert.Equal(0, VectorMath.Cosine(new[] { 0f, 0f }, new[] { 1f, 1f }));
}
