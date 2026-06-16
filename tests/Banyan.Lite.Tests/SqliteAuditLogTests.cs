// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Lite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Banyan.Lite.Tests;

public sealed class SqliteAuditLogTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"banyan-audit-{Guid.NewGuid():N}.db");
    private SqliteAuditLog _log = null!;

    public async ValueTask InitializeAsync()
        => _log = await SqliteAuditLog.OpenAsync($"Data Source={_dbPath}");

    public async ValueTask DisposeAsync()
    {
        await _log.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Append_LinksAndVerifies()
    {
        await _log.AppendAsync("agent-a", "memory.write", "mem-1", "ok", metadata: "ns=default");
        await _log.AppendAsync("agent-a", "memory.update", "mem-1", "ok");
        await _log.AppendAsync("agent-b", "memory.forget", "mem-1", "ok");

        var all = await _log.ReadAllAsync();
        Assert.Equal(new long[] { 1, 2, 3 }, all.Select(e => e.Seq));
        Assert.Equal(all[0].Hash, all[1].PrevHash);
        Assert.True((await _log.VerifyAsync()).Ok);
    }

    [Fact]
    public async Task Verify_DetectsTampering()
    {
        await _log.AppendAsync("agent-a", "memory.write", "mem-1", "ok");
        await _log.AppendAsync("agent-a", "memory.write", "mem-2", "ok");

        // Tamper directly in the DB without recomputing the hash.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE audit_log SET actor = 'mallory' WHERE seq = 1";
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await _log.VerifyAsync();
        Assert.False(result.Ok);
        Assert.Equal(1, result.BrokenSeq);
    }

    [Fact]
    public async Task ReopenedLog_ContinuesChain()
    {
        await _log.AppendAsync("agent-a", "memory.write", "mem-1", "ok");
        await _log.DisposeAsync();

        _log = await SqliteAuditLog.OpenAsync($"Data Source={_dbPath}");
        var e2 = await _log.AppendAsync("agent-a", "memory.write", "mem-2", "ok");

        Assert.Equal(2, e2.Seq);
        Assert.True((await _log.VerifyAsync()).Ok);
    }
}
