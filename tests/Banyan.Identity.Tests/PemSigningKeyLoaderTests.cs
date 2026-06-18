// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using Banyan.Identity.Crypto;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Banyan.Identity.Tests;

public sealed class PemSigningKeyLoaderTests : IDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-pem-" + Guid.NewGuid().ToString("N")[..8]);

    public PemSigningKeyLoaderTests() => Directory.CreateDirectory(_tmpDir);
    public void Dispose()             { try { Directory.Delete(_tmpDir, recursive: true); } catch { } }

    [Fact]
    public void Generate_WritesPkcs8Pem_AndIsLoadable()
    {
        var path = Path.Combine(_tmpDir, "key.pem");
        PemSigningKeyLoader.Generate(path);

        Assert.True(File.Exists(path));
        var pem = File.ReadAllText(path);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", pem);

        var (key, creds) = PemSigningKeyLoader.Load(path);
        Assert.NotNull(key.Rsa);
        Assert.Equal(SecurityAlgorithms.RsaSha256, creds.Algorithm);
        Assert.Equal(2048, key.KeySize);
    }

    [Fact]
    public void Generate_RefusesOverwrite_ByDefault()
    {
        var path = Path.Combine(_tmpDir, "key.pem");
        PemSigningKeyLoader.Generate(path);
        Assert.Throws<IOException>(() => PemSigningKeyLoader.Generate(path));
    }

    [Fact]
    public void Generate_OverwritesWhenForced()
    {
        var path = Path.Combine(_tmpDir, "key.pem");
        PemSigningKeyLoader.Generate(path);
        var first = File.ReadAllText(path);
        PemSigningKeyLoader.Generate(path, overwrite: true);
        var second = File.ReadAllText(path);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_SetsUnixMode_0600()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_tmpDir, "key.pem");
        PemSigningKeyLoader.Generate(path);
        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Load_KidIsDeterministicAcrossLoads()
    {
        var path = Path.Combine(_tmpDir, "key.pem");
        PemSigningKeyLoader.Generate(path);
        var (k1, _) = PemSigningKeyLoader.Load(path);
        var (k2, _) = PemSigningKeyLoader.Load(path);
        Assert.Equal(k1.KeyId, k2.KeyId);
        Assert.False(string.IsNullOrEmpty(k1.KeyId));
    }

    [Fact]
    public void RoundTrip_SignAndVerifyWorks()
    {
        var path = Path.Combine(_tmpDir, "key.pem");
        PemSigningKeyLoader.Generate(path);
        var (key, _) = PemSigningKeyLoader.Load(path);

        var data = "hello banyan"u8.ToArray();
        var sig  = key.Rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(key.Rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Generate_3072Bit_Works()
    {
        var path = Path.Combine(_tmpDir, "key3k.pem");
        PemSigningKeyLoader.Generate(path, bits: 3072);
        var (key, _) = PemSigningKeyLoader.Load(path);
        Assert.Equal(3072, key.KeySize);
    }
}
