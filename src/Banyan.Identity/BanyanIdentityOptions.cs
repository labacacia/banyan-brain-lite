// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Identity;

/// <summary>Configuration shape for the OLS-backed human identity track. See docs/architecture/identity.md.</summary>
public sealed class BanyanIdentityOptions
{
    public string DbPath { get; set; } = "~/.banyan/identity.db";
    public string SigningKeyPath { get; set; } = "~/.banyan/identity-signing.pem";
    /// <summary>
    /// OIDC issuer URL. The Banyan web server stamps tokens with this value, advertises it
    /// in <c>/.well-known/openid-configuration</c>, and rejects tokens whose <c>iss</c> claim
    /// disagrees. The CLI reads it to discover the token endpoint. In Lite single-host this
    /// must equal the URL the web server is actually listening on (the default
    /// <c>http://localhost:5180</c> matches <c>WebOptions.Urls</c>).
    /// </summary>
    public string Issuer { get; set; } = "http://localhost:5180";
    public string Audience { get; set; } = "banyan";
    public TimeSpan AccessTokenExpiry { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenExpiry { get; set; } = TimeSpan.FromDays(30);
    public string CliClientId { get; set; } = "banyan-cli";
    public IList<string> CliRedirectUris { get; set; } = ["http://127.0.0.1"];
}
