// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity;
using Ivy.Hub.Client;

namespace Banyan.Web.IvyHub;

public sealed record IvyHubProvisioningSyncResult(
    string TenantId,
    int Workspaces,
    int Users,
    int DisabledUsers,
    int AdminsBootstrapped,
    DateTimeOffset? GeneratedAt);

public sealed class IvyHubProvisioningSyncService(
    IBanyanIvyHubProvisioningClient client,
    SqliteIdentityStore identityStore)
{
    public async Task<IvyHubProvisioningSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var snapshot = await client.GetSnapshotAsync(ct);
        return await SyncAsync(snapshot, ct);
    }

    public async Task<IvyHubProvisioningSyncResult> SyncAsync(
        IvyHubProvisioningSnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var tenantId = Required(snapshot.Tenant.TenantId, "tenant.tenant_id");
        var users = 0;
        var disabled = 0;
        var admins = 0;
        foreach (var hubUser in snapshot.Users)
        {
            users++;
            if (hubUser.Disabled)
            {
                disabled++;
                continue;
            }

            var roles = hubUser.Roles ?? [];
            if (!roles.Contains("tenant-admin", StringComparer.OrdinalIgnoreCase)
                && !roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var username = FirstNonEmpty(hubUser.Username, hubUser.Email, hubUser.PrincipalId);
            await AdminBootstrapper.EnsureExternalAdminAsync(identityStore, username, hubUser.Email, ct);
            admins++;
        }

        return new IvyHubProvisioningSyncResult(
            TenantId: tenantId,
            Workspaces: snapshot.Workspaces.Count(),
            Users: users,
            DisabledUsers: disabled,
            AdminsBootstrapped: admins,
            GeneratedAt: snapshot.GeneratedAt);
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Ivy Hub provisioning snapshot is missing {field}.")
            : value;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "ivy-hub-user";
}
