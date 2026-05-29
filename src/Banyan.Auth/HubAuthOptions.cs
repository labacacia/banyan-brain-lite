// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Auth;

/// <summary>
/// Configuration for Hub IAM mode (<see cref="BanyanAuthMode.Hub"/>).
/// Bind from <c>Banyan:Hub</c> section or supply via CLI flags.
/// </summary>
public sealed class HubAuthOptions
{
    /// <summary>
    /// OIDC authority URL of the Hub identity provider.
    /// Used for JWT discovery + validation (OIDC metadata at <c>{JwtAuthority}/.well-known/openid-configuration</c>).
    /// </summary>
    public string? JwtAuthority { get; set; }

    /// <summary>
    /// Expected JWT audience claim. Defaults to <c>"banyan"</c> when empty.
    /// </summary>
    public string? JwtAudience { get; set; }

    /// <summary>
    /// Hub NIP CA NID (e.g. <c>urn:nps:ca:hub.acme:root</c>).
    /// Used as the trusted NID issuer for agent identity frames.
    /// </summary>
    public string? NidIssuerNid { get; set; }

    /// <summary>
    /// Hub NIP CA public key in <c>ed25519:&lt;base64&gt;</c> form.
    /// </summary>
    public string? NidPublicKey { get; set; }

    /// <summary>True when at least one Hub credential is configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrEmpty(JwtAuthority) || !string.IsNullOrEmpty(NidIssuerNid);
}
