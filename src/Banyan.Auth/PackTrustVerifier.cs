// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core.KnowledgePacks;

namespace Banyan.Auth;

public enum PackTrust { Trusted, Unsigned, UntrustedPublisher, BadSignature }

public sealed record PackTrustResult(PackTrust Outcome, string? PublisherNid, string? Reason)
{
    public bool Ok => Outcome == PackTrust.Trusted;
}

/// <summary>
/// Resolves a publisher NID to its trusted Ed25519 public key, or null when the NID
/// is unknown / untrusted / revoked. Production implementation is CA-backed
/// (<see cref="CaPublisherKeyResolver"/>) so pack trust reuses the existing NID/CA
/// trust chain; tests pass a stub.
/// </summary>
public interface IPublisherKeyResolver
{
    ValueTask<byte[]?> ResolvePublicKeyAsync(string publisherNid, CancellationToken ct = default);
}

/// <summary>
/// Verifies a <c>.banyanpack</c> v2's signature against the publisher NID named in
/// <c>manifest.sig</c>, where the publisher key is vouched for by the NID/CA trust
/// chain (KB-3 mount trust). Strict mode rejects unsigned/untrusted packs; lenient
/// mode only fails an actually-bad signature.
/// </summary>
public static class PackTrustVerifier
{
    public static async Task<PackTrustResult> VerifyAsync(
        Stream pack, IPublisherKeyResolver resolver, bool strict, CancellationToken ct = default)
    {
        var signature = await PackSigning.ReadSignatureAsync(pack, ct).ConfigureAwait(false);
        if (signature is null)
            return new PackTrustResult(PackTrust.Unsigned, null,
                strict ? "pack is unsigned and strict signing is required" : "pack is unsigned");

        var publisherNid = signature.KeyId;
        if (string.IsNullOrWhiteSpace(publisherNid))
            return new PackTrustResult(PackTrust.UntrustedPublisher, null, "signature has no publisher key id");

        var publicKey = await resolver.ResolvePublicKeyAsync(publisherNid, ct).ConfigureAwait(false);
        if (publicKey is null)
            return new PackTrustResult(PackTrust.UntrustedPublisher, publisherNid,
                "publisher NID is not trusted by the CA chain");

        using var signer = Ed25519PackSigner.FromPublicKey(publicKey, publisherNid);
        var verification = await PackSigning.VerifyAsync(pack, signer, ct).ConfigureAwait(false);
        return verification == PackVerification.Valid
            ? new PackTrustResult(PackTrust.Trusted, publisherNid, null)
            : new PackTrustResult(PackTrust.BadSignature, publisherNid, verification.ToString());
    }
}
