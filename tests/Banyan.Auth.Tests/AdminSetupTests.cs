// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Banyan.Web;
using Banyan.Web.Endpoints;
using Banyan.Web.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using InnoLotus.Root.Oidc.Extensions;
using Xunit;

namespace Banyan.Auth.Tests;

public sealed class AdminSetupTests : IAsyncLifetime
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-admin-setup-" + Guid.NewGuid().ToString("N")[..8]);

    private WebApplication? _app;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tmpDir);

        var signingKeyPath = Path.Combine(_tmpDir, "identity-signing.pem");
        var identityDbPath = Path.Combine(_tmpDir, "identity.db");
        PemSigningKeyLoader.Generate(signingKeyPath);

        var opts = new WebOptions
        {
            IdentityDbPath = identityDbPath,
            IdentitySigningKeyPath = signingKeyPath,
            Audience = "banyan-test",
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        const string testIssuer = "http://placeholder";
        builder.Services.AddSingleton(opts);
        builder.Services.AddBanyanIdentity(o =>
        {
            o.DbPath = identityDbPath;
            o.SigningKeyPath = signingKeyPath;
            o.Issuer = testIssuer;
            o.Audience = opts.Audience;
        });
        var (signingKey, _) = PemSigningKeyLoader.Load(signingKeyPath);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = testIssuer,
                    ValidateAudience = true,
                    ValidAudience = opts.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    RoleClaimType = "role",
                    NameClaimType = "unique_name",
                };
            });
        builder.Services.AddAuthorization(authz =>
        {
            authz.AddPolicy("admin", p => p.RequireRole("admin", "ADMIN"));
        });

        _app = builder.Build();
        var identityOpts = new BanyanIdentityOptions
        {
            DbPath = opts.IdentityDbPath,
            SigningKeyPath = opts.IdentitySigningKeyPath,
            Issuer = testIssuer,
            Audience = opts.Audience,
        };
        await AdminBootstrapper.EnsureBaselineAsync(_app.Services.GetRequiredService<SqliteIdentityStore>(), identityOpts);
        _app.UseSessionCookie();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapRootOidcEndpoints();
        SetupEndpoints.Map(_app);
        BrowserAuthEndpoints.Map(_app);

        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FirstRun_StatusRequiresSetup_ThenAdminCanBeCreatedAndLogin()
    {
        var status = await _http.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.True(status.GetProperty("setupRequired").GetBoolean());

        var setup = await _http.PostAsJsonAsync("/api/setup/admin", new
        {
            username = "admin",
            password = "Sup3r$ecret-2026",
        });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        status = await _http.GetFromJsonAsync<JsonElement>("/api/setup/status");
        Assert.False(status.GetProperty("setupRequired").GetBoolean());

        var login = await _http.PostAsJsonAsync("/api/auth/login", new
        {
            username = "admin",
            password = "Sup3r$ecret-2026",
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(BrowserAuthEndpoints.SessionCookieName + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetupAfterAdminExists_Returns409()
    {
        var first = await _http.PostAsJsonAsync("/api/setup/admin", new
        {
            username = "admin",
            password = "Sup3r$ecret-2026",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _http.PostAsJsonAsync("/api/setup/admin", new
        {
            username = "admin2",
            password = "An0ther$ecret-2026",
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
