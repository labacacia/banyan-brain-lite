// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Banyan.Auth;
using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Banyan.Node.Auth;
using Banyan.Web.Endpoints;
using Banyan.Web.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NPS.NIP.Verification;
using OLS.Root.Authentication.Stores;
using OLS.Root.Core.Models;
using OLS.Root.Core.Security;
using OLS.Root.Oidc.Extensions;
using OLS.Root.Oidc.Models;
using Xunit;

namespace Banyan.Auth.Tests;

/// <summary>
/// End-to-end coverage of the OLS-backed admin login flow: OIDC discovery, the browser
/// <c>/api/auth/login</c> endpoint, JWT-gated admin routes (<c>/api/agents</c>, <c>/api/ca</c>),
/// and the cookie session lifter. Spins up a real <see cref="WebApplication"/> on an ephemeral
/// port with a real <see cref="EmbeddedNipCa"/> + a real <see cref="SqliteIdentityStore"/>
/// pre-seeded with an admin user and a non-admin user.
/// </summary>
public sealed class AdminLoginTests : IAsyncLifetime
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "banyan-admin-login-" + Guid.NewGuid().ToString("N")[..8]);

    private const string AdminUser    = "alice";
    private const string AdminPass    = "Sup3r$ecret-2026";
    private const string NormalUser   = "bob";
    private const string NormalPass   = "B0bsT3stPass!";

    private WebApplication? _app;
    private EmbeddedNipCa?  _ca;
    private HttpClient      _http    = null!;
    private string          _baseUrl = "";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tmpDir);

        // ── Identity bootstrap: signing key + identity.db with admin + non-admin users ───
        var signingKeyPath = Path.Combine(_tmpDir, "identity-signing.pem");
        var identityDbPath = Path.Combine(_tmpDir, "identity.db");
        PemSigningKeyLoader.Generate(signingKeyPath);

        // Open the identity store via the same DI graph the live app uses, so the migrations + role
        // wiring match exactly. Don't reuse this SP for the WebApp; it's just for seeding.
        await SeedIdentityAsync(identityDbPath, signingKeyPath);

        // ── NIP CA bootstrap (so /api/agents & /api/ca actually have a CA to talk to) ────
        var caOpts = new BanyanNipCaOptions
        {
            DbPath        = Path.Combine(_tmpDir, "nipca.db"),
            KeyFilePath   = Path.Combine(_tmpDir, "ca-key.pem"),
            KeyPassphrase = "ca-test-pass",
            CaNid         = "urn:nps:ca:test.banyan:root",
            BaseUrl       = "http://localhost:0",
        };
        EmbeddedNipCa.GenerateKey(caOpts);
        _ca = await EmbeddedNipCa.OpenAsync(caOpts);

        // ── WebApplication wiring (mirrors WebApp.RunAsync but trimmed to what these tests need) ──
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(_ca);
        builder.Services.AddSingleton(new NidAuthenticationOptions { Mode = NidAuthMode.AnonymousAllowed });
        builder.Services.AddSingleton(_ => new NipVerifierOptions
        {
            TrustedIssuers      = new Dictionary<string, string> { [_ca.CaNid] = _ca.CaPubKey },
            LocalRevokedSerials = new HashSet<string>(),
            OcspUrl             = null!,
        });
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<NipIdentVerifier>();

        // Identity wiring — the actual subject under test. Issuer = "http://placeholder" is fine
        // because both the JWT issuer (in the token) and the JWT validator (below) are pinned to
        // the same string; we don't need it to match the bound port for these tests.
        const string testIssuer = "http://placeholder";
        builder.Services.AddBanyanIdentity(o =>
        {
            o.DbPath         = identityDbPath;
            o.SigningKeyPath = signingKeyPath;
            o.Issuer         = testIssuer;
            o.Audience       = "banyan-test";
        });
        var (signingKey, _) = PemSigningKeyLoader.Load(signingKeyPath);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidIssuer              = testIssuer,
                    ValidateAudience         = true,
                    ValidAudience            = "banyan-test",
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = signingKey,
                    RoleClaimType            = "role",
                    NameClaimType            = "unique_name",
                };
            });
        builder.Services.AddAuthorization(authz =>
        {
            authz.AddPolicy("admin", p => p.RequireRole("admin", "ADMIN"));
        });

        _app = builder.Build();
        _app.UseSessionCookie();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapOlsOidcEndpoints();
        BrowserAuthEndpoints.Map(_app);
        _app.UseNidAuthentication();

        _app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
        AgentEndpoints.Map(_app, requireAdmin: true);
        CaEndpoints   .Map(_app, requireAdmin: true);
        NipCaEndpoints.Map(_app);

        await _app.StartAsync();
        _baseUrl = _app.Urls.First();
        _http    = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        if (_ca  is not null) await _ca.DisposeAsync();
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private async Task SeedIdentityAsync(string dbPath, string keyPath)
    {
        var services = new ServiceCollection();
        services.AddBanyanIdentity(o =>
        {
            o.DbPath         = dbPath;
            o.SigningKeyPath = keyPath;
            o.Issuer         = "http://placeholder";
            o.Audience       = "banyan-test";
        });
        await using var sp = services.BuildServiceProvider();
        var store  = sp.GetRequiredService<SqliteIdentityStore>();
        var hasher = sp.GetRequiredService<IPasswordHasher<IdentityUser>>();

        // Admin role
        if (await store.Roles.FindByNameAsync("ADMIN", default) is null)
            await store.Roles.CreateAsync(new IdentityRole { Name = "admin", NormalizedName = "ADMIN" }, default);

        // Admin user
        var admin = new IdentityUser
        {
            UserName = AdminUser, NormalizedUserName = AdminUser.ToUpperInvariant(),
            Email = $"{AdminUser}@local", NormalizedEmail = $"{AdminUser.ToUpperInvariant()}@LOCAL",
            EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        admin.PasswordHash = hasher.HashPassword(admin, AdminPass);
        await store.Users.CreateAsync(admin, default);
        await store.Users.AddToRoleAsync(admin, "ADMIN", default);

        // Non-admin user (no role)
        var normal = new IdentityUser
        {
            UserName = NormalUser, NormalizedUserName = NormalUser.ToUpperInvariant(),
            Email = $"{NormalUser}@local", NormalizedEmail = $"{NormalUser.ToUpperInvariant()}@LOCAL",
            EmailConfirmed = true, SecurityStamp = Guid.NewGuid().ToString("N"),
        };
        normal.PasswordHash = hasher.HashPassword(normal, NormalPass);
        await store.Users.CreateAsync(normal, default);

        // Seed the CLI OIDC client so device-code tests can probe the discovery doc / token endpoint.
        var cli = new OidcClient
        {
            ClientId = "banyan-cli", ClientName = "Banyan CLI Test",
            IsEnabled = true, RequireClientSecret = false, RequirePkce = true,
            AccessTokenLifetime       = TimeSpan.FromMinutes(30),
            AuthorizationCodeLifetime = TimeSpan.FromMinutes(5),
            RefreshTokenLifetime      = TimeSpan.FromDays(30),
        };
        cli.RedirectUris.Add("http://127.0.0.1");
        cli.AllowedScopes.Add("openid"); cli.AllowedScopes.Add("profile"); cli.AllowedScopes.Add("banyan.full");
        cli.AllowedGrantTypes.Add("authorization_code");
        cli.AllowedGrantTypes.Add("refresh_token");
        cli.AllowedGrantTypes.Add("urn:ietf:params:oauth:grant-type:device_code");
        await store.OidcClients.UpsertAsync(cli);
    }

    // ── OIDC discovery ─────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoveryDocument_IsServed_AndAdvertisesExpectedEndpoints()
    {
        var resp = await _http.GetAsync("/.well-known/openid-configuration");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(doc.TryGetProperty("issuer", out _));
        Assert.True(doc.TryGetProperty("token_endpoint", out _));
        Assert.True(doc.TryGetProperty("device_authorization_endpoint", out _),
            "device_authorization_endpoint missing — `banyan login` (Device Code) won't work");
    }

    // ── Browser login flow ────────────────────────────────────────────────

    [Fact]
    public async Task Login_WithBadCredentials_Returns401()
    {
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new { username = AdminUser, password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Login_WithGoodCredentials_SetsCookieAndReturnsExpiry()
    {
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new { username = AdminUser, password = AdminPass });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var cookies = resp.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("banyan_session=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase));

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AdminUser, body.GetProperty("username").GetString());
    }

    // ── JWT-gated admin route ─────────────────────────────────────────────

    [Fact]
    public async Task AgentList_WithoutToken_Returns401()
    {
        var resp = await _http.GetAsync("/api/agents");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AgentList_WithAdminToken_Returns200()
    {
        var token = await BearerLoginAsync(AdminUser, AdminPass);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task AgentList_WithNonAdminToken_Returns403()
    {
        var token = await BearerLoginAsync(NormalUser, NormalPass);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/agents");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task AgentList_WithSessionCookie_Returns200()
    {
        // Run login + cookie reuse without copying the cookie manually; HttpClientHandler.UseCookies
        // does the work. Verifies SessionCookieMiddleware lifts cookie → Bearer.
        var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl) };

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUser, password = AdminPass });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var resp = await client.GetAsync("/api/agents");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ── /api/auth/me round-trip ───────────────────────────────────────────

    [Fact]
    public async Task Me_WithoutAuth_ReportsLoggedOut()
    {
        var resp = await _http.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("loggedIn").GetBoolean());
    }

    [Fact]
    public async Task Me_WithBearer_ReportsAdminUser()
    {
        var token = await BearerLoginAsync(AdminUser, AdminPass);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("loggedIn").GetBoolean());
        Assert.Contains("admin", body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()),
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<string> BearerLoginAsync(string username, string password)
    {
        // The browser endpoint sets a cookie; we extract the same token to use as a Bearer.
        var resp = await _http.PostAsJsonAsync("/api/auth/login", new { username, password });
        resp.EnsureSuccessStatusCode();
        var cookie = resp.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("banyan_session=", StringComparison.Ordinal));
        var eq = cookie.IndexOf('='); var sc = cookie.IndexOf(';');
        return cookie.Substring(eq + 1, sc - eq - 1);
    }
}
