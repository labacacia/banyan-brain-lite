// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Banyan.Auth;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace Banyan.Web;

internal sealed record LocalAgentIdentity(
    string? Nid,
    string? Serial,
    string? ExpiresAt,
    string? PrivateKeyBase64)
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static LocalAgentIdentity Empty { get; } = new(null, null, null, null);

    public static async Task<LocalAgentIdentity> EnsureAsync(EmbeddedNipCa ca, WebOptions opts, CancellationToken ct)
    {
        var path = WebOptions.ExpandHome(opts.LocalAgentProfilePath);
        var existing = await TryReadValidAsync(path, ca, ct);
        if (existing is not null)
            return existing;

        var algo = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var pubKey = NipSigner.EncodePublicKey(key.PublicKey);
        var privRaw = key.Export(KeyBlobFormat.RawPrivateKey);

        var frame = await ca.RegisterAgentAsync(
            opts.LocalAgentId,
            pubKey,
            ["memory.read", "memory.write", "mcp"],
            ct);

        var identity = new LocalAgentIdentity(
            frame.Nid,
            frame.Serial,
            frame.ExpiresAt,
            Convert.ToBase64String(privRaw));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(identity, s_json), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return identity;
    }

    private static async Task<LocalAgentIdentity?> TryReadValidAsync(string path, EmbeddedNipCa ca, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var identity = JsonSerializer.Deserialize<LocalAgentIdentity>(
                await File.ReadAllTextAsync(path, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (string.IsNullOrWhiteSpace(identity?.Nid) || string.IsNullOrWhiteSpace(identity.PrivateKeyBase64))
                return null;

            var verification = await ca.VerifyAsync(identity.Nid, ct);
            return verification.Valid ? identity : null;
        }
        catch
        {
            return null;
        }
    }
}
