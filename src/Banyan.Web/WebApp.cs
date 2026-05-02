using Banyan.Auth;
using Banyan.Core;
using Banyan.Embedders;
using Banyan.Identity;
using Banyan.Identity.Crypto;
using Banyan.Identity.Extensions;
using Banyan.Lite;
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
    /// CA is opened only when <see cref="WebOptions.OpenCa"/> is true and the
    /// <c>BANYAN_NIP_CA_PASSPHRASE</c> env var is set.
    /// </summary>
    public static async Task RunAsync(WebOptions opts, string[]? rawArgs = null, CancellationToken ct = default)
    {
        var passphrase = Environment.GetEnvironmentVariable("BANYAN_NIP_CA_PASSPHRASE");

        // ContentRoot must be Banyan.Web.dll's directory (so wwwroot/ is found) regardless of which
        // process invoked us — when called from `banyan web`, Banyan.Web.dll lives in CLI's bin/.
        var contentRoot = Path.GetDirectoryName(typeof(WebApp).Assembly.Location)!;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args            = rawArgs ?? Array.Empty<string>(),
            ContentRootPath = contentRoot,
            WebRootPath     = Path.Combine(contentRoot, "wwwroot"),
        });
        builder.WebHost.UseUrls(opts.Urls);
        builder.Services.AddSingleton(opts);

        var memoryDb = WebOptions.ExpandHome(opts.MemoryDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(memoryDb)!);
        IEmbedder embedder = EmbedderFactory.Create();
        var memoryStore = await SqliteMemoryStore.OpenAsync(
            $"Data Source={memoryDb}", embedder, opts.SqliteVecLibPath, ct);
        builder.Services.AddSingleton(embedder);
        builder.Services.AddSingleton(memoryStore);
        if (memoryStore.VecEnabled)
            Console.WriteLine($"[store] sqlite-vec ANN index ready: embeddings_vec");

        if (opts.OpenCa)
        {
            if (string.IsNullOrEmpty(passphrase))
                Console.Error.WriteLine("BANYAN_NIP_CA_PASSPHRASE not set; skipping CA. Pass --no-ca to silence, or set the env var to expose /api/agents and /api/ca.");
            else
            {
                var caOpts = new BanyanNipCaOptions
                {
                    DbPath        = opts.NipCaDbPath,
                    KeyFilePath   = opts.NipCaKeyPath,
                    KeyPassphrase = passphrase,
                    CaNid         = opts.CaNid,
                };
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
            }
        }

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

        builder.Services.AddSingleton(new NidAuthenticationOptions { Mode = opts.NidAuthMode });

        // ── Human identity (OLS-backed OIDC) ──────────────────────────────────────────
        // Wire the OLS pipeline only when the operator has bootstrapped identity.db /
        // signing key via `banyan keygen` + `banyan init`. Skipping cleanly when those
        // are missing keeps `banyan web` usable as a zero-config demo.
        var identityReady = ShouldEnableIdentity(opts);
        if (identityReady)
        {
            // Issuer is pinned to the actual listening URL — discovery doc, JWT `iss`,
            // and JWT-validator expected issuer must agree, and the listening URL is the
            // single source of truth in Lite single-host.
            var issuer = opts.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries).First().TrimEnd('/');
            builder.Services.AddBanyanIdentity(o =>
            {
                o.DbPath              = opts.IdentityDbPath;
                o.SigningKeyPath      = opts.IdentitySigningKeyPath;
                o.Issuer              = issuer;
                o.Audience            = opts.Audience;
                o.AccessTokenExpiry   = opts.AccessTokenExpiry;
                o.RefreshTokenExpiry  = opts.RefreshTokenExpiry;
                o.CliClientId         = opts.CliClientId;
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
        }

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        if (identityReady)
        {
            // Cookie-to-bearer lifter must run BEFORE UseAuthentication so the JWT validator
            // sees the synthesised header.
            app.UseSessionCookie();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapOlsOidcEndpoints();
            BrowserAuthEndpoints.Map(app);
        }

        // Mount NID auth only when there's a verifier (i.e. CA was loaded). Otherwise we have no
        // trust anchors and the middleware would 401 every request.
        if (app.Services.GetService<NipIdentVerifier>() is not null)
            app.UseNidAuthentication();

        app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = "P1.5-demo" }));
        MemoryEndpoints  .Map(app);
        IdentityEndpoints.Map(app);
        CaEndpoints      .Map(app, identityReady);
        // Agent + NIP-CA HTTP endpoints depend on the CA being loaded (DI activation fails otherwise).
        if (app.Services.GetService<EmbeddedNipCa>() is not null)
        {
            AgentEndpoints.Map(app, identityReady);
            NipCaEndpoints.Map(app);
        }

        Console.WriteLine($"Banyan demo web UI listening on {opts.Urls}");
        Console.WriteLine($"  memory.db : {memoryDb}");
        if (app.Services.GetService<EmbeddedNipCa>() is { } liveCa)
            Console.WriteLine($"  CA NID    : {liveCa.CaNid}");
        Console.WriteLine($"  identity  : {(identityReady ? "enabled (OIDC + JWT, admin routes gated)" : "disabled — run `banyan keygen` + `banyan init` to enable")}");

        await app.RunAsync(ct);
    }

    private static bool ShouldEnableIdentity(WebOptions opts)
    {
        var dbPath  = WebOptions.ExpandHome(opts.IdentityDbPath);
        var keyPath = WebOptions.ExpandHome(opts.IdentitySigningKeyPath);
        return File.Exists(dbPath) && File.Exists(keyPath);
    }
}
