namespace Banyan.Identity;

/// <summary>Configuration shape for the OLS-backed human identity track. See docs/architecture/identity.md.</summary>
public sealed class BanyanIdentityOptions
{
    public string DbPath { get; set; } = "~/.banyan/identity.db";
    public string SigningKeyPath { get; set; } = "~/.banyan/identity-signing.pem";
    public string Issuer { get; set; } = "https://localhost:5001";
    public string Audience { get; set; } = "banyan";
    public TimeSpan AccessTokenExpiry { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenExpiry { get; set; } = TimeSpan.FromDays(30);
    public string CliClientId { get; set; } = "banyan-cli";
    public IList<string> CliRedirectUris { get; set; } = ["http://127.0.0.1"];
}
