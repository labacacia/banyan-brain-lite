// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Options;

namespace Banyan.Web.IvyHub;

public static class IvyHubServiceCollectionExtensions
{
    public static IServiceCollection AddBanyanIvyHub(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultAudience)
    {
        services.AddOptions<IvyHubOptions>().Configure(options =>
        {
            configuration.GetSection(IvyHubOptions.SectionName).Bind(options);
            if (Uri.TryCreate(Environment.GetEnvironmentVariable("IVY_HUB_ISSUER"), UriKind.Absolute, out var issuer))
                options.Issuer = issuer;
            options.Audience = FirstNonEmpty(
                Environment.GetEnvironmentVariable("IVY_HUB_AUDIENCE"),
                options.Audience,
                defaultAudience);
            options.ClientId = FirstNonEmpty(
                Environment.GetEnvironmentVariable("IVY_HUB_CLIENT_ID"),
                options.ClientId,
                defaultAudience);
            options.ProvisioningToken = FirstNonEmpty(
                Environment.GetEnvironmentVariable("IVY_HUB_PROVISIONING_TOKEN"),
                options.ProvisioningToken);
        });

        services.AddOptions<BanyanSsoOptions>().Configure<IOptions<IvyHubOptions>>((options, ivy) =>
        {
            configuration.GetSection(BanyanSsoOptions.SectionName).Bind(options);
            if (options.Providers.Count == 0 && ivy.Value.Issuer is not null)
            {
                options.Providers.Add(new BanyanSsoProviderOptions
                {
                    Id = "ivy-hub",
                    DisplayName = "Ivy Hub",
                    Type = "ivy-hub-local",
                    Authority = ivy.Value.Issuer.ToString(),
                    ClientId = ivy.Value.ClientId,
                    Enabled = true,
                });
            }
        });

        services.AddHttpClient<IBanyanIvyHubProvisioningClient, BanyanIvyHubProvisioningClient>();
        services.AddScoped<IvyHubProvisioningSyncService>();
        services.AddHostedService<IvyHubProvisioningHostedService>();
        return services;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
