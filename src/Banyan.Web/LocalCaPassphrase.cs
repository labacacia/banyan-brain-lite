// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Banyan.Auth;

namespace Banyan.Web;

/// <summary>
/// Resolves the embedded CA key passphrase for a local Lite host. Precedence:
/// <c>BANYAN_NIP_CA_PASSPHRASE</c> env var → stored secret file → auto-generate.
/// <para>
/// When auto-generating, the secret is protected at rest: on Windows via DPAPI
/// (<see cref="DataProtectionScope.CurrentUser"/>, so the blob is bound to the user/machine
/// and unreadable by other accounts or off-box); on other platforms the BCL has no portable
/// secret store, so the file is written <c>0600</c> and operators are advised to supply
/// <c>BANYAN_NIP_CA_PASSPHRASE</c> for hardened/multi-user hosts (then nothing is written to disk).
/// </para>
/// </summary>
internal static class LocalCaPassphrase
{
    private const string DpapiPrefix = "dpapi:";

    public static string? Resolve(WebOptions opts)
    {
        var env = Environment.GetEnvironmentVariable("BANYAN_NIP_CA_PASSPHRASE");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        var keyPath    = WebOptions.ExpandHome(opts.NipCaKeyPath);
        var secretPath = WebOptions.ExpandHome(opts.NipCaPassphrasePath);
        if (File.Exists(secretPath))
            return Unprotect(File.ReadAllText(secretPath).Trim());

        // An existing key with no passphrase source means the caller must supply one explicitly.
        if (File.Exists(keyPath))
            return null;

        var passphrase = GeneratePassphrase();
        var dir = Path.GetDirectoryName(secretPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(secretPath, Protect(passphrase));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.Error.WriteLine(
                $"[nip-ca] generated a local CA passphrase at {secretPath} (perms 0600). " +
                "On this platform it is not OS-encrypted at rest — for hardened or multi-user hosts, " +
                "set BANYAN_NIP_CA_PASSPHRASE instead so the passphrase is never written to disk.");
        }

        return passphrase;
    }

    public static void EnsureKey(BanyanNipCaOptions caOpts)
    {
        var keyPath = WebOptions.ExpandHome(caOpts.KeyFilePath);
        if (!File.Exists(keyPath))
            EmbeddedNipCa.GenerateKey(caOpts);
    }

    private static string GeneratePassphrase()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Protects the passphrase for at-rest storage. DPAPI on Windows; plaintext (0600) elsewhere.</summary>
    private static string Protect(string passphrase)
    {
        if (OperatingSystem.IsWindows())
        {
            var blob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(passphrase), optionalEntropy: null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(blob);
        }
        return passphrase;
    }

    /// <summary>Reverses <see cref="Protect"/>; tolerates legacy plaintext files (no <c>dpapi:</c> prefix).</summary>
    private static string Unprotect(string stored)
    {
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            var b64 = stored[DpapiPrefix.Length..];
            if (OperatingSystem.IsWindows())
            {
                var plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(b64), optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            // A DPAPI blob is only readable on the Windows user/machine that wrote it.
            throw new InvalidOperationException(
                "CA passphrase file is DPAPI-protected but this is not Windows. " +
                "Provide BANYAN_NIP_CA_PASSPHRASE or regenerate the CA on this host.");
        }
        return stored;
    }
}
