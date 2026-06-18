// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Core;
using Banyan.Core.KnowledgePacks;
using Banyan.Embedders;
using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Banyan.Lite;
using Banyan.Mcp;
using Banyan.Node.Auth;
using Banyan.Web.Components;
using Banyan.Web.Endpoints;
using Banyan.Web.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NPS.NIP.Verification;
using OLS.Root.Oidc.Extensions;

namespace Banyan.Web;

public static class WebApp
{
    /// <summary>
    /// Build and run the Banyan demo web app. Reuses the same flag set as <c>banyan web</c>.
    /// CA is opened only when <see cref="WebOptions.OpenCa"/> is true. Lite auto-creates
    /// a local passphrase and CA key on first launch unless an existing key requires an
    /// explicit <c>BANYAN_NIP_CA_PASSPHRASE</c>.
    /// </summary>
    public static async Task RunAsync(WebOptions opts, string[]? rawArgs = null, CancellationToken ct = default)
    {
        // AppContext.BaseDirectory is the directory of the host executable regardless of whether
        // the app is single-file or multi-file, replacing Assembly.Location which is empty in SFAs.
        var contentRoot = AppContext.BaseDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args            = rawArgs ?? Array.Empty<string>(),
            ContentRootPath = contentRoot,
            WebRootPath     = Path.Combine(contentRoot, "wwwroot"),
        });
        builder.WebHost.UseUrls(opts.Urls);
        builder.Services.AddSingleton(opts);
        builder.Services.AddHttpClient();
        builder.Services.AddProblemDetails();

        var memoryDb = WebOptions.ExpandHome(opts.MemoryDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(memoryDb)!);
        IEmbedder embedder = EmbedderFactory.Create();
        var memoryStore = await SqliteMemoryStore.OpenAsync(
            $"Data Source={memoryDb}", embedder, opts.SqliteVecLibPath, ct: ct);
        builder.Services.AddSingleton(embedder);
        builder.Services.AddSingleton(memoryStore);
        builder.Services.AddSingleton<IMemoryPoolRepository>(
            await SqliteMemoryPoolRepository.OpenAsync($"Data Source={memoryDb}", ct));
        if (memoryStore.VecEnabled)
            Console.WriteLine($"[store] sqlite-vec ANN index ready: embeddings_vec");

        // Knowledge Pack: registry + recall store wrapping the raw memory store.
        Directory.CreateDirectory(WebOptions.ExpandHome(opts.PackStorePath));
        var packRegistry = new FileKnowledgePackMountRegistry(WebOptions.ExpandHome(opts.PackRegistryPath));
        builder.Services.AddSingleton(packRegistry);
        builder.Services.AddSingleton<IMemoryStore>(new KnowledgePackRecallStore(memoryStore, packRegistry));

        LocalAgentIdentity localAgent = LocalAgentIdentity.Empty;
        if (opts.CaServerType == CaServerMode.External)
        {
            opts.OpenCa = false;
            var probe = await CaServerProbe.TestExternalAsync(opts.ExternalCaServerAddress, ct: ct);
            if (probe.Ok && probe.CaNid is not null && probe.PublicKey is not null)
            {
                opts.TrustedIssuers.Clear();
                opts.TrustedIssuers[probe.CaNid] = probe.PublicKey;
                Console.WriteLine($"[nid]  external CA: {probe.Address} ({probe.CaNid})");
            }
            else
            {
                Console.Error.WriteLine($"External CA server unavailable; NID auth disabled. {probe.Message}");
            }
        }

        if (opts.OpenCa && opts.CaServerType == CaServerMode.Embedded)
        {
            var passphrase = LocalCaPassphrase.Resolve(opts);
            if (string.IsNullOrEmpty(passphrase))
                Console.Error.WriteLine("Existing CA key requires BANYAN_NIP_CA_PASSPHRASE; skipping CA. Pass --no-ca to silence, or set the env var to expose /api/agents and /api/ca.");
            else
            {
                var caOpts = new BanyanNipCaOptions
                {
                    DbPath        = opts.NipCaDbPath,
                    KeyFilePath   = opts.NipCaKeyPath,
                    KeyPassphrase = passphrase,
                    CaNid         = opts.CaNid,
                };
                LocalCaPassphrase.EnsureKey(caOpts);
                var ca = await EmbeddedNipCa.OpenAsync(caOpts, ct);
                builder.Services.AddSingleton(ca);
                // OcspUrl=null disables remote OCSP — the embedded CA is the source of truth and
                // NidAuthenticationMiddleware consults it directly for revocation. An empty string
                // here would crash HttpClient ("invalid request URI") on every verify.
                builder.Services.AddSingleton(_ => new NipVerifierOptions
                {
                    TrustedIssuers      = new Dictionary<string, string> { [ca.CaNid] = ca.CaPubKey },
                    LocalRevokedSerials = new HashSet<string>(),
                    OcspUrl             = null!,
                });
                builder.Services.AddHttpClient();
                builder.Services.AddSingleton<NipIdentVerifier>();
                localAgent = await LocalAgentIdentity.EnsureAsync(ca, opts, ct);
                Console.WriteLine($"[nid]  local agent: {localAgent.Nid}");
            }
        }
        builder.Services.AddSingleton(localAgent);
        builder.Services.AddSingleton<McpClientBrandRegistry>();

        // ── External CA trust anchors (--trusted-issuer) ──────────────────────────────
        // When running without an embedded CA, operators can supply trust anchors for
        // one or more external nip-ca-server instances. NID auth middleware will then
        // verify certs cryptographically (and optionally via OCSP) against those CAs.
        // Silently skipped when the embedded CA is already wired (avoids double-registration).
        if (opts.TrustedIssuers.Count > 0 && builder.Services.All(s => s.ServiceType != typeof(NipVerifierOptions)))
        {
            builder.Services.AddSingleton(_ => new NipVerifierOptions
            {
                TrustedIssuers      = opts.TrustedIssuers,
                LocalRevokedSerials = new HashSet<string>(),
                OcspUrl             = opts.ExternalOcspUrl ?? null!,
            });
            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<NipIdentVerifier>();
            Console.WriteLine($"[nid]  trusting {opts.TrustedIssuers.Count} external CA(s): {string.Join(", ", opts.TrustedIssuers.Keys)}");
            if (opts.ExternalOcspUrl is not null)
                Console.WriteLine($"[nid]  OCSP: {opts.ExternalOcspUrl}");
        }

        builder.Services.AddSingleton(new NidAuthenticationOptions
        {
            Mode = opts.NidAuthMode,
                PublicPaths =
                [
                "/api/health", "/health", "/alive",
                "/api/setup/status", "/api/setup/admin",
                "/api/auth/login", "/api/auth/logout", "/api/auth/me",
                "/.nwm", "/.schema",
                "/v1/ca/cert", "/v1/crl", "/.well-known/nps-ca",
                // Blazor Server infrastructure
                "/_blazor", "/_framework",
            ],
        });

        // ── Human identity (OLS-backed OIDC) ──────────────────────────────────────────
        // Web startup now owns first-run identity bootstrap: the signing key and
        // identity.db are created if missing, then the browser setup flow creates
        // the first admin user before the main UI is allowed through.
        var issuer = opts.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries).First().TrimEnd('/');
        var identityOpts = new BanyanIdentityOptions
        {
            DbPath = opts.IdentityDbPath,
            SigningKeyPath = opts.IdentitySigningKeyPath,
            Issuer = issuer,
            Audience = opts.Audience,
            AccessTokenExpiry = opts.AccessTokenExpiry,
            RefreshTokenExpiry = opts.RefreshTokenExpiry,
            CliClientId = opts.CliClientId,
        };
        AdminBootstrapper.EnsureSigningKey(identityOpts);
        var identityDbDir = Path.GetDirectoryName(WebOptions.ExpandHome(opts.IdentityDbPath));
        if (!string.IsNullOrEmpty(identityDbDir))
            Directory.CreateDirectory(identityDbDir);
        builder.Services.AddBanyanIdentity(o =>
        {
            o.DbPath              = identityOpts.DbPath;
            o.SigningKeyPath      = identityOpts.SigningKeyPath;
            o.Issuer              = identityOpts.Issuer;
            o.Audience            = identityOpts.Audience;
            o.AccessTokenExpiry   = identityOpts.AccessTokenExpiry;
            o.RefreshTokenExpiry  = identityOpts.RefreshTokenExpiry;
            o.CliClientId         = identityOpts.CliClientId;
            o.CliRedirectUris     = identityOpts.CliRedirectUris;
        });
        // OLS issues JWTs but doesn't register a validation scheme — wire JWT Bearer with the
        // same signing key + issuer + audience so /api/* routes can require [Authorize].
        var (signingKey, _) = PemSigningKeyLoader.Load(WebOptions.ExpandHome(opts.IdentitySigningKeyPath));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Disable the default JwtSecurityTokenHandler inbound claim-name mapping
                // (e.g. "role" → ClaimTypes.Role) so claims stay verbatim — keeps RoleClaimType
                // below matching what OLS actually puts on the wire.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer           = true,
                    ValidIssuer              = issuer,
                    ValidateAudience         = true,
                    ValidAudience            = opts.Audience,
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
        builder.Services.AddAntiforgery();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddAntDesign();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services
            .AddSingleton(new McpDefaults("default"))
            .AddSingleton<IBanyanMcpAgentContext, McpHttpAgentContext>()
            .AddMcpServer()
            .WithHttpTransport(o =>
            {
                o.Stateless = true;
            })
            .WithTools<BanyanMemoryTools>();

        var app = builder.Build();
        await AdminBootstrapper.EnsureBaselineAsync(
            app.Services.GetRequiredService<SqliteIdentityStore>(), identityOpts, ct);

        app.Use(async (ctx, next) =>
        {
            if (HttpMethods.IsGet(ctx.Request.Method)
                && ctx.Request.Path == "/"
                && !await AdminBootstrapper.HasAdminAsync(ctx.RequestServices.GetRequiredService<SqliteIdentityStore>(), ctx.RequestAborted))
            {
                ctx.Response.Redirect("/setup");
                return;
            }

            await next();
        });

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseStaticFiles();

        // Cookie-to-bearer lifter must run BEFORE UseAuthentication so the JWT validator
        // sees the synthesised header.
        app.UseSessionCookie();
        app.UseAuthentication();
        app.Use(async (ctx, next) =>
        {
            if (IsBrowserPageRequest(ctx)
                && ctx.User.Identity?.IsAuthenticated != true
                && await AdminBootstrapper.HasAdminAsync(
                    ctx.RequestServices.GetRequiredService<SqliteIdentityStore>(),
                    ctx.RequestAborted))
            {
                ctx.Response.Redirect("/login");
                return;
            }

            await next();
        });
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapOlsOidcEndpoints();
        BrowserAuthEndpoints.Map(app);
        SetupEndpoints.Map(app);

        // Mount NID auth only when there's a verifier (i.e. CA was loaded). Otherwise we have no
        // trust anchors and the middleware would 401 every request.
        if (app.Services.GetService<NipIdentVerifier>() is not null)
            app.UseNidAuthentication();
        app.UseMiddleware<McpClientBrandMiddleware>();

        var version = typeof(WebApp).Assembly.GetName().Version?.ToString() ?? "dev";
        app.MapGet("/api/health", () => Results.Ok(new { ok = true, version }));
        HealthEndpoints.Map(app);
        app.MapMcp("/mcp");
        MemoryEndpoints        .Map(app);
        KnowledgePackEndpoints .Map(app);
        IdentityEndpoints      .Map(app);
        CaEndpoints      .Map(app, requireAdmin: true);
        // Agent + NIP-CA HTTP endpoints depend on the CA being loaded (DI activation fails otherwise).
        if (app.Services.GetService<EmbeddedNipCa>() is not null)
        {
            AgentEndpoints.Map(app, requireAdmin: true);
            NipCaEndpoints.Map(app, mapHealth: false);
        }

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        Console.WriteLine($"Banyan demo web UI listening on {opts.Urls}");
        Console.WriteLine($"  memory.db : {memoryDb}");
        if (app.Services.GetService<EmbeddedNipCa>() is { } liveCa)
            Console.WriteLine($"  CA NID    : {liveCa.CaNid}");
        Console.WriteLine("  identity  : enabled (first-run admin setup enforced)");

        await app.RunAsync(ct);
    }

    private static bool IsBrowserPageRequest(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method))
            return false;

        var path = ctx.Request.Path.Value ?? "/";
        if (path is "/login" or "/setup")
            return false;

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/.well-known/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
            || path is "/alive" or "/health" or "/.nwm" or "/.schema")
            return false;

        return !Path.HasExtension(path);
    }
}
