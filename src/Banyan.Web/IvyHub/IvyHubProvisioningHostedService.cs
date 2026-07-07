// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Options;

namespace Banyan.Web.IvyHub;

public sealed class IvyHubProvisioningHostedService(
    IServiceProvider services,
    IOptions<IvyHubOptions> options,
    ILogger<IvyHubProvisioningHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Provisioning.Enabled)
            return;

        try
        {
            await using var scope = services.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<IvyHubProvisioningSyncService>();
            var result = await sync.SyncAsync(stoppingToken);
            logger.LogInformation(
                "Ivy Hub provisioning sync completed: tenant={TenantId}, workspaces={Workspaces}, users={Users}, disabled={DisabledUsers}, adminsBootstrapped={AdminsBootstrapped}.",
                result.TenantId,
                result.Workspaces,
                result.Users,
                result.DisabledUsers,
                result.AdminsBootstrapped);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ivy Hub provisioning sync failed at startup.");
            throw;
        }
    }
}
