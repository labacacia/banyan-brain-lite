using System.Security.Cryptography;

namespace Banyan.Core.KnowledgePacks;

/// <summary>
/// AES-256-GCM encryption with PBKDF2-SHA256 key derivation for .banyanpack containers.
/// Wire format for encrypted payloads: 12-byte nonce || ciphertext || 16-byte GCM tag.
/// </summary>
public static class KnowledgePackEncryption
{
    public const string AlgorithmAes256Gcm = "aes-256-gcm";
    public const string KdfPbkdf2Sha256    = "pbkdf2-sha256";
    public const int    DefaultIterations  = 310_000;   // OWASP 2023 recommendation

    private const int SaltBytes  = 32;
    private const int NonceBytes = 12;
    private const int KeyBytes   = 32;
    private const int TagBytes   = 16;

    public static byte[] GenerateSalt()
        => RandomNumberGenerator.GetBytes(SaltBytes);

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(
            passphrase, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);

    /// <summary>Returns nonce || ciphertext || tag.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var output = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(output, 0);
        ciphertext.CopyTo(output, NonceBytes);
        tag.CopyTo(output, NonceBytes + ciphertext.Length);
        return output;
    }

    /// <summary>
    /// Decrypts a payload produced by <see cref="Encrypt"/>.
    /// Throws <see cref="CryptographicException"/> on authentication failure (wrong key/tampered data).
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> data, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (data.Length < NonceBytes + TagBytes)
            throw new CryptographicException("Encrypted payload is too short.");

        var nonce      = data[..NonceBytes];
        var tag        = data[(data.Length - TagBytes)..];
        var ciphertext = data[NonceBytes..(data.Length - TagBytes)];
        var plaintext  = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
