// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Banyan.Core.KnowledgePacks;
using NSec.Cryptography;

namespace Banyan.Auth;

/// <summary>
/// Ed25519 <see cref="IPackSigner"/> for <c>.banyanpack</c> v2 (KB-3). Implemented
/// outside Banyan.Core (which stays zero-dependency) using NSec. A full key
/// (32-byte seed) can sign and verify; a public-key-only instance verifies packs
/// from a trusted publisher. HSM/KMS is a future provider on the same interface.
/// </summary>
public sealed class Ed25519PackSigner : IPackSigner, IDisposable
{
    private readonly Key? _key;            // present when this signer can sign
    private readonly PublicKey _publicKey; // always present (verify)

    public string Algorithm => "ed25519";
    public string? KeyId { get; }

    private Ed25519PackSigner(Key? key, PublicKey publicKey, string? keyId)
    {
        _key = key;
        _publicKey = publicKey;
        KeyId = keyId;
    }

    /// <summary>Signing + verifying instance from a 32-byte private seed.</summary>
    public static Ed25519PackSigner FromSeed(ReadOnlySpan<byte> seed, string? keyId = null)
    {
        var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
        return new Ed25519PackSigner(key, key.PublicKey, keyId);
    }

    /// <summary>Verify-only instance from a 32-byte public key (trusted publisher).</summary>
    public static Ed25519PackSigner FromPublicKey(ReadOnlySpan<byte> publicKey, string? keyId = null)
    {
        var pub = PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey);
        return new Ed25519PackSigner(null, pub, keyId);
    }

    /// <summary>The raw 32-byte public key (to publish as a trust anchor).</summary>
    public byte[] ExportPublicKey() => _publicKey.Export(KeyBlobFormat.RawPublicKey);

    public string Sign(string digest)
    {
        if (_key is null)
            throw new InvalidOperationException("This Ed25519PackSigner is verify-only (no private key).");
        var sig = SignatureAlgorithm.Ed25519.Sign(_key, Encoding.UTF8.GetBytes(digest));
        return Convert.ToHexString(sig).ToLowerInvariant();
    }

    public bool Verify(string digest, string signature, string? keyId)
    {
        byte[] sig;
        try { sig = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        return SignatureAlgorithm.Ed25519.Verify(_publicKey, Encoding.UTF8.GetBytes(digest), sig);
    }

    public void Dispose() => _key?.Dispose();
}
