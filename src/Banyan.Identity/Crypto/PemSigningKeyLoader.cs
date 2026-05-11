using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Banyan.Identity.Crypto;

/// <summary>
/// Loads/generates RSA private keys in PKCS#8 PEM format and adapts them to
/// <see cref="RsaSecurityKey"/> + <see cref="SigningCredentials"/> for use by
/// OLS.Root.Authentication (JwtOptions.SigningKey) and OLS.Root.Oidc (OidcOptions.SigningCredentials).
/// </summary>
public static class PemSigningKeyLoader
{
    /// <summary>Load an RSA private key from a PEM file. The <see cref="RsaSecurityKey.KeyId"/> is set to a deterministic 8-byte SHA-256 thumbprint of the public key (base64url, no padding).</summary>
    public static (RsaSecurityKey Key, SigningCredentials Credentials) Load(string path)
    {
        var pem = File.ReadAllText(path);
        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var key = new RsaSecurityKey(rsa) { KeyId = ComputeKid(rsa) };
        var creds = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        return (key, creds);
    }

    /// <summary>Generate a fresh RSA private key, write it as PKCS#8 PEM at <paramref name="outPath"/>, and (on Unix) set 0600 permissions. Refuses to overwrite an existing file unless <paramref name="overwrite"/> is true.</summary>
    public static void Generate(string outPath, int bits = 2048, bool overwrite = false)
    {
        if (File.Exists(outPath) && !overwrite)
            throw new IOException($"Refusing to overwrite existing key at {outPath}. Pass --force to override.");

        using var rsa = RSA.Create(bits);
        var pem = rsa.ExportPkcs8PrivateKeyPem();

        var dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(outPath, pem);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(outPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string ComputeKid(RSA rsa)
    {
        var pub  = rsa.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(pub);
        return Convert.ToBase64String(hash.AsSpan(0, 8))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
