// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Auth;

/// <summary>
/// <see cref="IPublisherKeyResolver"/> backed by the NIP CA (KB-3 mount trust): a
/// publisher NID is trusted iff the CA verifies it (valid, not revoked/expired);
/// its Ed25519 public key comes straight from the cert record. This is how pack
/// trust reuses the existing NID/CA trust chain.
/// </summary>
public sealed class CaPublisherKeyResolver(EmbeddedNipCa ca) : IPublisherKeyResolver
{
    public async ValueTask<byte[]?> ResolvePublicKeyAsync(string publisherNid, CancellationToken ct = default)
    {
        var result = await ca.VerifyAsync(publisherNid, ct).ConfigureAwait(false);
        if (!result.Valid || result.Record?.PubKey is not { Length: > 0 } encoded)
            return null;

        var key = TryDecode(encoded);
        return key is { Length: 32 } ? key : null; // Ed25519 raw public key is 32 bytes
    }

    private static byte[]? TryDecode(string s)
    {
        try { return Convert.FromBase64String(s); } catch (FormatException) { }
        try { return Convert.FromHexString(s); } catch (FormatException) { }
        return null;
    }
}
