// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using Banyan.Auth;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Auth.Tests;

public class Ed25519PackSignerTests
{
    private static byte[] Seed(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static MemoryStream Pack()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (p, c) in new[] { ("manifest.json", "{\"pack_id\":\"p1\"}"), ("memories/records.jsonl", "x\ny") })
            {
                var e = zip.CreateEntry(p, CompressionLevel.NoCompression);
                using var s = e.Open();
                s.Write(Encoding.UTF8.GetBytes(c));
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Sign_And_Verify_RoundTrip()
    {
        using var signer = Ed25519PackSigner.FromSeed(Seed(1), "node-1");
        using var pack = Pack();
        await PackSigning.SignAsync(pack, signer);
        Assert.Equal(PackVerification.Valid, await PackSigning.VerifyAsync(pack, signer));
    }

    [Fact]
    public async Task PublicKeyOnly_CanVerify_Publisher_Pack()
    {
        using var signer = Ed25519PackSigner.FromSeed(Seed(7), "pub-1");
        using var pack = Pack();
        await PackSigning.SignAsync(pack, signer);

        using var verifier = Ed25519PackSigner.FromPublicKey(signer.ExportPublicKey(), "pub-1");
        Assert.Equal(PackVerification.Valid, await PackSigning.VerifyAsync(pack, verifier));
        Assert.Throws<InvalidOperationException>(() => verifier.Sign("deadbeef"));
    }

    [Fact]
    public async Task DifferentKey_FailsVerification()
    {
        using var signer = Ed25519PackSigner.FromSeed(Seed(1));
        using var other = Ed25519PackSigner.FromSeed(Seed(2));
        using var pack = Pack();
        await PackSigning.SignAsync(pack, signer);
        Assert.Equal(PackVerification.BadSignature, await PackSigning.VerifyAsync(pack, other));
    }
}
