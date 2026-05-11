// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using Banyan.Auth;

namespace Banyan.Web;

internal static class LocalCaPassphrase
{
    public static string? Resolve(WebOptions opts)
    {
        var env = Environment.GetEnvironmentVariable("BANYAN_NIP_CA_PASSPHRASE");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        var keyPath = WebOptions.ExpandHome(opts.NipCaKeyPath);
        var secretPath = WebOptions.ExpandHome(opts.NipCaPassphrasePath);
        if (File.Exists(secretPath))
            return File.ReadAllText(secretPath).Trim();

        if (File.Exists(keyPath))
            return null;

        var passphrase = GeneratePassphrase();
        var dir = Path.GetDirectoryName(secretPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(secretPath, passphrase);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

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
}
