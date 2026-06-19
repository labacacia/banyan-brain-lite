// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Xunit;

namespace Banyan.Core.Tests;

public class DomainTests
{
    [Fact]
    public void MemoryId_New_ReturnsDistinctValues()
    {
        var id1 = MemoryId.New();
        var id2 = MemoryId.New();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void EventId_New_ReturnsDistinctValues()
    {
        var id1 = EventId.New();
        var id2 = EventId.New();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void SearchQuery_Defaults_AreCorrect()
    {
        var q = new SearchQuery("find something");
        Assert.Equal(SearchMode.Hybrid, q.Mode);
        Assert.Equal(10, q.K);
        Assert.Null(q.Namespace);
    }

    [Fact]
    public void WriteRequest_DefaultNamespace_IsDefault()
    {
        var req = new WriteRequest("some content");
        Assert.Equal("default", req.Namespace);
        Assert.Null(req.Metadata);
        Assert.Null(req.AgentNid);
    }

    [Fact]
    public void MemoryEventType_HasExpectedValues()
    {
        Assert.Equal(0, (int)MemoryEventType.Write);
        Assert.Equal(1, (int)MemoryEventType.Update);
        Assert.Equal(2, (int)MemoryEventType.Tombstone);
    }
}
