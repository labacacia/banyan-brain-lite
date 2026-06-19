// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Auth;

/// <summary>
/// Banyan-side configuration for the NID Certificate Authority track.
/// Bridges Banyan defaults (storage paths, env-var switching) to NPS.NIP's <see cref="NPS.NIP.Ca.NipCaOptions"/>.
/// </summary>
public sealed class BanyanNipCaOptions
{
    /// <summary>Path to the SQLite NIP-CA database (default <c>~/.banyan/nipca.db</c>).</summary>
    public string DbPath { get; set; } = "~/.banyan/nipca.db";

    /// <summary>Path to the Ed25519 CA private key (PEM, encrypted with <see cref="KeyPassphrase"/>). Default <c>~/.banyan/nipca-key.pem</c>.</summary>
    public string KeyFilePath { get; set; } = "~/.banyan/nipca-key.pem";

    /// <summary>Passphrase used to encrypt/decrypt the CA private key. Required.</summary>
    public string KeyPassphrase { get; set; } = "";

    /// <summary>NID of this CA, e.g. <c>urn:nps:ca:local.banyan:root</c>.</summary>
    public string CaNid { get; set; } = "urn:nps:ca:local.banyan:root";

    /// <summary>Public-facing base URL of the CA. Used in published frames as the issuer reference.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5180";

    /// <summary>Human-readable display name shown in CA discovery responses.</summary>
    public string DisplayName { get; set; } = "Banyan Local NIP CA";

    /// <summary>Validity windows (days).</summary>
    public int AgentCertValidityDays  { get; set; } = 30;
    public int NodeCertValidityDays   { get; set; } = 365;
    public int RenewalWindowDays      { get; set; } = 7;

    /// <summary>Optional remote CA URL. When set, <see cref="BanyanNipCa.Open"/> returns a remote client (deferred to P3); otherwise an embedded CA backed by <see cref="DbPath"/>.</summary>
    public string? RemoteCaUrl { get; set; }
}
