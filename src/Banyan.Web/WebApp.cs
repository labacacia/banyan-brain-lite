using Banyan.Auth;
using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Banyan.Web.Endpoints;
using Banyan.Web.Middleware;
using NPS.NIP.Verification;

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
                // Register the NIP verifier + the in-process CA as a trusted issuer.
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
        builder.Services.AddSingleton(new NidAuthenticationOptions { Mode = opts.NidAuthMode });

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        // Mount NID auth only when there's a verifier (i.e. CA was loaded). Otherwise we have no
        // trust anchors and the middleware would 401 every request.
        if (app.Services.GetService<NipIdentVerifier>() is not null)
            app.UseNidAuthentication();

        app.MapGet("/api/health", () => Results.Ok(new { ok = true, version = "P1.5-demo" }));
        MemoryEndpoints  .Map(app);
        IdentityEndpoints.Map(app);
        CaEndpoints      .Map(app);
        // Agent + NIP-CA HTTP endpoints depend on the CA being loaded (DI activation fails otherwise).
        if (app.Services.GetService<EmbeddedNipCa>() is not null)
        {
            AgentEndpoints.Map(app);
            NipCaEndpoints.Map(app);
        }

        Console.WriteLine($"Banyan demo web UI listening on {opts.Urls}");
        Console.WriteLine($"  memory.db : {memoryDb}");
        if (app.Services.GetService<EmbeddedNipCa>() is { } liveCa)
            Console.WriteLine($"  CA NID    : {liveCa.CaNid}");

        await app.RunAsync(ct);
    }
}
