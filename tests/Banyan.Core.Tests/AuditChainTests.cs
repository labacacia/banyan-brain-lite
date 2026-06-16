// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Xunit;

namespace Banyan.Core.Tests;

public class AuditChainTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static List<AuditEntry> BuildChain(int n, IAuditSigner? signer = null)
    {
        var entries = new List<AuditEntry>();
        AuditEntry? prev = null;
        for (var i = 0; i < n; i++)
        {
            prev = AuditChain.AppendEntry(prev, T0.AddSeconds(i),
                actor: $"agent-{i}", action: "memory.write", target: $"mem-{i}",
                result: "ok", metadata: null, signer: signer);
            entries.Add(prev);
        }
        return entries;
    }

    [Fact]
    public void FirstLink_UsesGenesisPrevHash()
    {
        var e = AuditChain.AppendEntry(null, T0, "a", "memory.write", "m", "ok");
        Assert.Equal(AuditChain.Genesis, e.PrevHash);
        Assert.Equal(1, e.Seq);
    }

    [Fact]
    public void Chain_Links_SequentialSeqAndPrevHash()
    {
        var c = BuildChain(3);
        Assert.Equal(new long[] { 1, 2, 3 }, c.Select(e => e.Seq));
        Assert.Equal(c[0].Hash, c[1].PrevHash);
        Assert.Equal(c[1].Hash, c[2].PrevHash);
    }

    [Fact]
    public void Verify_IntactChain_Ok()
        => Assert.True(AuditChain.Verify(BuildChain(5)).Ok);

    [Fact]
    public void Verify_TamperedContent_FailsAtThatLink()
    {
        var c = BuildChain(4);
        c[2] = c[2] with { Action = "memory.forget" }; // tamper without recomputing hash
        var r = AuditChain.Verify(c);
        Assert.False(r.Ok);
        Assert.Equal(3, r.BrokenSeq);
        Assert.Contains("tampered", r.Reason);
    }

    [Fact]
    public void Verify_BrokenLinkage_Fails()
    {
        var c = BuildChain(3);
        c.RemoveAt(1); // drop the middle link → linkage breaks at the next one
        var r = AuditChain.Verify(c);
        Assert.False(r.Ok);
        Assert.Equal(3, r.BrokenSeq);
        Assert.Contains("linkage", r.Reason);
    }

    [Fact]
    public void Verify_SignedChain_OkWithSameSigner()
    {
        var signer = new HmacSigner("key-1");
        Assert.True(AuditChain.Verify(BuildChain(3, signer), signer).Ok);
    }

    [Fact]
    public void Verify_SignedChain_FailsWithDifferentSigner()
    {
        var c = BuildChain(3, new HmacSigner("key-1"));
        var r = AuditChain.Verify(c, new HmacSigner("key-2"));
        Assert.False(r.Ok);
        Assert.Contains("signature", r.Reason);
    }

    // Minimal HMAC signer for tests (Ent's real signer is Ed25519 node key, OBS-7).
    private sealed class HmacSigner(string key) : IAuditSigner
    {
        public string Sign(string hash)
        {
            using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hash)));
        }

        public bool VerifySignature(string hash, string signature)
            => string.Equals(Sign(hash), signature, StringComparison.Ordinal);
    }
}
