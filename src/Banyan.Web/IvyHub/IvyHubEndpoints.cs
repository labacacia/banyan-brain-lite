// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http.Json;
using System.Security.Claims;
using Banyan.Identity;
using Banyan.Identity.Crypto;
using Ivy.Hub.Client;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Banyan.Web.IvyHub;

public sealed record IvyHubEndpointOptions(
    string SessionCookieName,
    string SigningKeyPath,
    string Issuer,
    string Audience,
    TimeSpan AccessTokenExpiry,
    SameSiteMode SameSite = SameSiteMode.Strict,
    Action? MarkAdminReady = null);

public static class IvyHubEndpoints
{
    public sealed record LoginRequest(string Username, string Password);

    public static void MapAuth(RouteGroupBuilder group, IvyHubEndpointOptions endpointOptions)
    {
        group.MapGet("/sso/providers", (IOptions<BanyanSsoOptions> sso) =>
        {
            var providers = sso.Value.Providers
                .Where(p => p.Enabled && IsSupportedSsoProvider(p))
                .Select(p => new
                {
                    id = p.Id,
                    displayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName,
                    type = p.Type,
                    authority = p.Authority,
                    enabled = p.Enabled,
                });
            return Results.Ok(providers);
        }).AllowAnonymous();

        group.MapPost("/sso/login/{providerId}", async (
            string providerId,
            LoginRequest body,
            IOptions<BanyanSsoOptions> sso,
            SqliteIdentityStore identityStore,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var provider = sso.Value.Providers.FirstOrDefault(p =>
                p.Enabled &&
                string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                return Results.NotFound(new { error = "SSO provider not found." });
            if (!string.Equals(provider.Type, "ivy-hub-local", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "SSO provider does not support password exchange login." });
            if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
                return Results.BadRequest(new { error = "Username and password are required." });

            return await LoginWithIvyHubLocalAsync(provider, body, endpointOptions, identityStore, ctx, ct);
        }).AllowAnonymous();
    }

    public static void MapProvisioning(WebApplication app, bool requireAdmin = true)
    {
        var endpoint = app.MapPost("/v1/ivy-hub/provisioning/sync", async (
            IvyHubProvisioningSyncService sync,
            CancellationToken ct) =>
        {
            try
            {
                var result = await sync.SyncAsync(ct);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IvyHubClientException or HttpRequestException or TaskCanceledException)
            {
                return Results.Json(
                    new { error = "Ivy Hub provisioning sync failed.", details = ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
        if (requireAdmin)
            endpoint.RequireAuthorization("admin");
    }

    private static async Task<IResult> LoginWithIvyHubLocalAsync(
        BanyanSsoProviderOptions provider,
        LoginRequest body,
        IvyHubEndpointOptions endpointOptions,
        SqliteIdentityStore identityStore,
        HttpContext ctx,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider.Authority))
            return Results.BadRequest(new { error = "Ivy Hub provider authority is not configured." });

        var baseUri = provider.Authority.TrimEnd('/');
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var response = await client.PostAsJsonAsync(
                $"{baseUri}/v1/local-identity/login",
                new
                {
                    login = body.Username,
                    password = body.Password,
                    expiresInSeconds = (int)Math.Min(endpointOptions.AccessTokenExpiry.TotalSeconds, 86400),
                },
                ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return Results.Json(new { error = "Ivy Hub login failed.", details = text }, statusCode: StatusCodes.Status401Unauthorized);

            var hubLogin = System.Text.Json.JsonSerializer.Deserialize<IvyHubLocalLoginResponse>(
                text,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (hubLogin is null || string.IsNullOrWhiteSpace(hubLogin.PrincipalId))
                return Results.Json(new { error = "Ivy Hub login returned an invalid identity." }, statusCode: StatusCodes.Status502BadGateway);

            var standardIdentity = await TryLoadStandardHubIdentityAsync(provider, hubLogin.Token, endpointOptions, client, ct);
            var principalId = standardIdentity?.PrincipalId ?? hubLogin.PrincipalId;
            var username = FirstNonEmpty(standardIdentity?.DisplayName, hubLogin.DisplayName, principalId);
            var tenantId = FirstNonEmpty(standardIdentity?.TenantId, hubLogin.TenantId);
            var workspaceId = standardIdentity?.WorkspaceId ?? hubLogin.WorkspaceId;
            var localUser = await AdminBootstrapper.EnsureExternalAdminAsync(identityStore, username, null, ct);
            endpointOptions.MarkAdminReady?.Invoke();

            var claims = new List<Claim>
            {
                new("sub", username),
                new(ClaimTypes.NameIdentifier, localUser.Id),
                new(ClaimTypes.Name, username),
                new("unique_name", username),
                new("role", "admin"),
                new(ClaimTypes.Role, "admin"),
                new("role", "ADMIN"),
                new("capability", "admin"),
                new("identity_provider", provider.Id),
                new("external_subject", principalId),
                new("tenant_id", tenantId),
            };
            if (!string.IsNullOrWhiteSpace(workspaceId))
                claims.Add(new Claim("workspace_id", workspaceId));
            claims.AddRange(provider.Capabilities
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => new Claim("capability", c)));

            var token = IssueToken(endpointOptions, claims);
            var expires = DateTimeOffset.UtcNow.Add(endpointOptions.AccessTokenExpiry);
            ctx.Response.Cookies.Append(
                endpointOptions.SessionCookieName,
                token,
                SessionCookieOptions(ctx, endpointOptions, expires));

            return Results.Ok(new
            {
                username,
                adminId = $"ivy-hub:{principalId}",
                source = provider.Id,
                tenantId,
                workspaceId,
                expiresAt = expires,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or IvyHubClientException)
        {
            return Results.Json(new { error = "Ivy Hub login exchange failed.", details = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IvyHubStandardIdentity?> TryLoadStandardHubIdentityAsync(
        BanyanSsoProviderOptions provider,
        string? accessToken,
        IvyHubEndpointOptions endpointOptions,
        HttpClient http,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Split('.').Length != 3)
            return null;
        if (!Uri.TryCreate(provider.Authority, UriKind.Absolute, out var issuer))
            return null;

        var audience = string.IsNullOrWhiteSpace(provider.ClientId)
            ? endpointOptions.Audience
            : provider.ClientId;
        var hub = new IvyHubClient(http, new IvyHubClientOptions
        {
            Issuer = issuer,
            ClientId = string.IsNullOrWhiteSpace(provider.ClientId) ? audience : provider.ClientId,
            Audience = audience,
        });
        var validated = await hub.ValidateJwtAsync(accessToken, audience, ct);
        var userInfo = await hub.GetUserInfoAsync(accessToken, ct);

        return new IvyHubStandardIdentity(
            PrincipalId: userInfo.Sub ?? validated.Subject,
            DisplayName: FirstNonEmpty(userInfo.Name, userInfo.PreferredUsername, userInfo.Email, validated.Subject),
            TenantId: FirstNonEmpty(userInfo.TenantId, ClaimValue(validated, "tenant_id"), ClaimValue(validated, "tenant")),
            WorkspaceId: FirstNonEmpty(userInfo.WorkspaceId, ClaimValue(validated, "workspace_id"), ClaimValue(validated, "workspace")));
    }

    private static string IssueToken(IvyHubEndpointOptions options, IReadOnlyList<Claim> claims)
    {
        var (_, credentials) = PemSigningKeyLoader.Load(options.SigningKeyPath);
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Expires = DateTime.UtcNow.Add(options.AccessTokenExpiry),
            SigningCredentials = credentials,
            Subject = new ClaimsIdentity(claims),
        });
    }

    private static CookieOptions SessionCookieOptions(
        HttpContext ctx,
        IvyHubEndpointOptions options,
        DateTimeOffset expires) => new()
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps || string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase),
            SameSite = options.SameSite,
            Expires = expires,
            MaxAge = expires - DateTimeOffset.UtcNow,
            Path = "/",
            IsEssential = true,
        };

    private static bool IsSupportedSsoProvider(BanyanSsoProviderOptions provider) =>
        string.Equals(provider.Type, "ivy-hub-local", StringComparison.OrdinalIgnoreCase);

    private static string? ClaimValue(IvyHubValidatedToken token, string claim) =>
        token.Claims.TryGetValue(claim, out var value) ? value?.ToString() : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}

internal sealed record IvyHubLocalLoginResponse(
    string PrincipalId,
    string? DisplayName,
    string TenantId,
    string? WorkspaceId,
    string Token,
    DateTimeOffset ExpiresAt,
    string? AuthStrength = null);

internal sealed record IvyHubStandardIdentity(
    string PrincipalId,
    string DisplayName,
    string TenantId,
    string? WorkspaceId);
