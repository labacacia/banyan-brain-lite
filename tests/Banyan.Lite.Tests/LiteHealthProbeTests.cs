// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Lite;
using Xunit;

namespace Banyan.Lite.Tests;

public class LiteHealthProbeTests
{
    [Fact]
    public void CheckResources_NoDbPath_ReportsMemoryOnly()
    {
        var check = LiteHealthProbe.CheckResources(null);
        Assert.Equal("ok", check.Status);
        Assert.Contains("rss=", check.Message);
        Assert.Contains("disk_free=n/a", check.Message!);
    }

    [Fact]
    public void CheckResources_WithDbPath_ReportsDbSizeAndDisk()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"banyan-health-{Guid.NewGuid():N}.db");
        File.WriteAllBytes(tmp, new byte[2048]);
        try
        {
            var check = LiteHealthProbe.CheckResources(tmp);
            Assert.Equal("ok", check.Status); // temp dir realistically has > 100MB free
            Assert.Contains("db=", check.Message!);
            Assert.DoesNotContain("disk_free=n/a", check.Message!);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
