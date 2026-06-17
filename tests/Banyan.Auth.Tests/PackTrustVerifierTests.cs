// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using Banyan.Auth;
using Banyan.Core.KnowledgePacks;
using Xunit;

namespace Banyan.Auth.Tests;

public class PackTrustVerifierTests
{
    private const string PublisherNid = "urn:nps:agent:acme:publisher";

    private static byte[] Seed(byte b) => Enumerable.Repeat(b, 32).ToArray();

    private static MemoryStream Pack()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(Encoding.UTF8.GetBytes("{\"pack_id\":\"p1\"}"));
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class StubResolver(string nid, byte[]? key) : IPublisherKeyResolver
    {
        public ValueTask<byte[]?> ResolvePublicKeyAsync(string publisherNid, CancellationToken ct = default)
            => ValueTask.FromResult(publisherNid == nid ? key : null);
    }

    [Fact]
    public async Task TrustedPublisher_SignedPack_IsTrusted()
    {
        using var signer = Ed25519PackSigner.FromSeed(Seed(1), PublisherNid);
        using var pack = Pack();
        await PackSigning.SignAsync(pack, signer);

        var resolver = new StubResolver(PublisherNid, signer.ExportPublicKey());
        var result = await PackTrustVerifier.VerifyAsync(pack, resolver, strict: true);

        Assert.True(result.Ok);
        Assert.Equal(PublisherNid, result.PublisherNid);
    }

    [Fact]
    public async Task UnknownPublisher_IsUntrusted()
    {
        using var signer = Ed25519PackSigner.FromSeed(Seed(1), PublisherNid);
        using var pack = Pack();
        await PackSigning.SignAsync(pack, signer);

        var resolver = new StubResolver("urn:nps:agent:other", new byte[32]);
        var result = await PackTrustVerifier.VerifyAsync(pack, resolver, strict: true);

        Assert.Equal(PackTrust.UntrustedPublisher, result.Outcome);
    }

    [Fact]
    public async Task WrongKey_ForPublisher_IsBadSignature()
    {
        using var signer = Ed25519PackSigner.FromSeed(Seed(1), PublisherNid);
        using var wrong = Ed25519PackSigner.FromSeed(Seed(2));
        using var pack = Pack();
        await PackSigning.SignAsync(pack, signer);

        var resolver = new StubResolver(PublisherNid, wrong.ExportPublicKey());
        var result = await PackTrustVerifier.VerifyAsync(pack, resolver, strict: true);

        Assert.Equal(PackTrust.BadSignature, result.Outcome);
    }

    [Fact]
    public async Task UnsignedPack_StrictFails_LenientPasses()
    {
        var resolver = new StubResolver(PublisherNid, new byte[32]);
        using var pack = Pack();

        var strict = await PackTrustVerifier.VerifyAsync(pack, resolver, strict: true);
        Assert.Equal(PackTrust.Unsigned, strict.Outcome);
        Assert.False(strict.Ok);

        pack.Position = 0;
        var lenient = await PackTrustVerifier.VerifyAsync(pack, resolver, strict: false);
        Assert.Equal(PackTrust.Unsigned, lenient.Outcome);
    }
}
