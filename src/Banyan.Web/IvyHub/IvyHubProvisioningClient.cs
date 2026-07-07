// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Ivy.Hub.Client;
using Microsoft.Extensions.Options;

namespace Banyan.Web.IvyHub;

public interface IBanyanIvyHubProvisioningClient
{
    Task<IvyHubProvisioningSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public sealed class BanyanIvyHubProvisioningClient(
    HttpClient http,
    IOptions<IvyHubOptions> options) : IBanyanIvyHubProvisioningClient
{
    public async Task<IvyHubProvisioningSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        if (opts.Issuer is null)
            throw new InvalidOperationException("IvyHub:Issuer is required for provisioning sync.");

        var token = Environment.GetEnvironmentVariable("IVY_HUB_PROVISIONING_TOKEN")
                    ?? opts.ProvisioningToken;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("IvyHub:ProvisioningToken or IVY_HUB_PROVISIONING_TOKEN is required.");

        var hub = new IvyHubClient(http, new IvyHubClientOptions
        {
            Issuer = opts.Issuer,
            Audience = string.IsNullOrWhiteSpace(opts.Audience) ? "banyan-lite" : opts.Audience,
            ClientId = string.IsNullOrWhiteSpace(opts.ClientId) ? "banyan-lite" : opts.ClientId,
        });

        return await hub.GetProvisioningSnapshotAsync(token, ct);
    }
}
