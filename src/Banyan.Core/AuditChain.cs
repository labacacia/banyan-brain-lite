// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;

namespace Banyan.Core;

/// <summary>One link in a tamper-evident audit chain (OBS-2).</summary>
public sealed record AuditEntry(
    long Seq,
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    string Target,
    string Result,
    string? Metadata,
    string PrevHash,
    string Hash,
    string? Signature = null);

/// <summary>Result of verifying a chain; <see cref="BrokenSeq"/> is the first bad link.</summary>
public sealed record AuditVerifyResult(bool Ok, long? BrokenSeq, string? Reason)
{
    public static readonly AuditVerifyResult Valid = new(true, null, null);
}

/// <summary>
/// Optional signer for audit links. The shared chain only hashes; an edition can
/// plug a signer (e.g. node Ed25519 key) so links are non-repudiable as well as
/// tamper-evident. Used by Ent (OBS-7); Lite/Pro can run hash-only.
/// </summary>
public interface IAuditSigner
{
    string Sign(string hash);
    bool VerifySignature(string hash, string signature);
}

/// <summary>
/// Pure, storage-agnostic tamper-evident audit log (OBS-2). Each link binds the
/// previous link's hash, so altering any earlier record invalidates every link
/// after it. Storage (SQLite for Lite, Postgres for Pro/Ent) lives in the
/// editions; this type owns only the hashing/linking/verification.
/// </summary>
public static class AuditChain
{
    /// <summary>PrevHash of the first link.</summary>
    public static readonly string Genesis = new('0', 64);

    /// <summary>Computes the canonical SHA-256 hash for a link. Length-prefixed fields prevent ambiguity.</summary>
    public static string ComputeHash(
        long seq, DateTimeOffset timestamp, string actor, string action,
        string target, string result, string? metadata, string prevHash)
    {
        var sb = new StringBuilder();
        Append(sb, seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(sb, timestamp.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Append(sb, actor);
        Append(sb, action);
        Append(sb, target);
        Append(sb, result);
        Append(sb, metadata ?? "");
        Append(sb, prevHash);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Builds the next link from the previous one (null = first link).</summary>
    public static AuditEntry AppendEntry(
        AuditEntry? prev, DateTimeOffset timestamp, string actor, string action,
        string target, string result, string? metadata = null, IAuditSigner? signer = null)
    {
        var seq = (prev?.Seq ?? 0) + 1;
        var prevHash = prev?.Hash ?? Genesis;
        var hash = ComputeHash(seq, timestamp, actor, action, target, result, metadata, prevHash);
        var signature = signer?.Sign(hash);
        return new AuditEntry(seq, timestamp, actor, action, target, result, metadata, prevHash, hash, signature);
    }

    /// <summary>Verifies linkage, recomputed hashes, and (when a signer is given) signatures.</summary>
    public static AuditVerifyResult Verify(IReadOnlyList<AuditEntry> entries, IAuditSigner? signer = null)
    {
        var expectedPrev = Genesis;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];

            if (e.PrevHash != expectedPrev)
                return new AuditVerifyResult(false, e.Seq, "prev-hash linkage broken");

            var recomputed = ComputeHash(e.Seq, e.Timestamp, e.Actor, e.Action, e.Target, e.Result, e.Metadata, e.PrevHash);
            if (recomputed != e.Hash)
                return new AuditVerifyResult(false, e.Seq, "content hash mismatch (tampered)");

            if (signer is not null && (e.Signature is null || !signer.VerifySignature(e.Hash, e.Signature)))
                return new AuditVerifyResult(false, e.Seq, "signature invalid");

            expectedPrev = e.Hash;
        }
        return AuditVerifyResult.Valid;
    }

    private static void Append(StringBuilder sb, string value)
        => sb.Append(value.Length).Append(':').Append(value).Append('|');
}
