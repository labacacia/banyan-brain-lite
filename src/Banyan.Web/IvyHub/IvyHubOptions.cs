// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Web.IvyHub;

public sealed class IvyHubOptions
{
    public const string SectionName = "IvyHub";

    public Uri? Issuer { get; set; }
    public string Audience { get; set; } = "banyan-lite";
    public string ClientId { get; set; } = "banyan-lite";
    public string? ProvisioningToken { get; set; }
    public IvyHubProvisioningSection Provisioning { get; set; } = new();
}

public sealed class IvyHubProvisioningSection
{
    public bool Enabled { get; set; }
}

public sealed class BanyanSsoOptions
{
    public const string SectionName = "Banyan:Sso";

    public List<BanyanSsoProviderOptions> Providers { get; set; } = [];
}

public sealed class BanyanSsoProviderOptions
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Type { get; set; } = "ivy-hub-local";
    public string? Authority { get; set; }
    public string ClientId { get; set; } = "";
    public string[] Capabilities { get; set; } = ["admin"];
    public bool Enabled { get; set; } = true;
}
