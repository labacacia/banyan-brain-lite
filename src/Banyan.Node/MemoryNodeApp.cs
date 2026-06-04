using Banyan.Auth;
using Banyan.Core;
using Banyan.Embedders;
using Banyan.Lite;
using Banyan.Node.Auth;
using NPS.NIP.Verification;
using NPS.NWP.Extensions;
using NPS.NWP.MemoryNode;
using NPS.NWP.Nwm;

namespace Banyan.Node;

/// <summary>
/// Memory Node host — wires Banyan storage / embedder / CA to the standard NPS.NWP pipeline.
/// All wire-format heavy lifting (frame parsing, NID auth, NPT token metering, NWM manifest)
/// is delegated to NPS middleware; we only supply <see cref="BanyanMemoryProvider"/>.
/// </summary>
public static class MemoryNodeApp
{
    public static async Task RunAsync(BanyanNodeOptions opts, string[]? rawArgs = null, CancellationToken ct = default)
    {
        var passphrase = Environment.GetEnvironmentVariable("BANYAN_NIP_CA_PASSPHRASE");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args            = rawArgs ?? Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls(opts.Urls);
        builder.Services.AddSingleton(opts);
        builder.Services.AddHttpClient();

        // ── Memory store (with embedder + sqlite-vec when available) ─────────
        var memoryDb = ExpandHome(opts.MemoryDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(memoryDb)!);
        IEmbedder embedder = EmbedderFactory.Create();
        var memoryStore = await SqliteMemoryStore.OpenAsync(
            $"Data Source={memoryDb}", embedder, opts.SqliteVecLibPath, ct: ct);
        builder.Services.AddSingleton(embedder);
        builder.Services.AddSingleton(memoryStore);

        // ── Embedded CA — auto-trusts itself when present ────────────────────
        EmbeddedNipCa? ca = null;
        var caKeyPath = ExpandHome(opts.NipCaKeyPath);
        if (!string.IsNullOrEmpty(passphrase) && File.Exists(caKeyPath))
        {
            ca = await EmbeddedNipCa.OpenAsync(new BanyanNipCaOptions
            {
                DbPath        = opts.NipCaDbPath,
                KeyFilePath   = opts.NipCaKeyPath,
                KeyPassphrase = passphrase,
                CaNid         = opts.CaNid,
            }, ct);
            opts.TrustedIssuers[ca.CaNid] = ca.CaPubKey;
            builder.Services.AddSingleton(ca);
        }

        // ── NIP verifier (used by NPS.NWP middleware for IdentFrame auth) ───
        // OcspUrl=null disables remote OCSP — embedded CA (when present) is consulted directly
        // by NidAuthenticationMiddleware; an empty string here would crash HttpClient on every verify.
        builder.Services.AddSingleton(_ => new NipVerifierOptions
        {
            TrustedIssuers      = opts.TrustedIssuers.ToDictionary(kv => kv.Key, kv => kv.Value),
            LocalRevokedSerials = new HashSet<string>(),
            OcspUrl             = null!,
        });
        builder.Services.AddSingleton<NipIdentVerifier>();

        // ── NWP DI: Memory Node provider + global NWP options ────────────────
        builder.Services.AddProblemDetails();
        builder.Services.AddNwp(o =>
        {
            o.DefaultLimit       = opts.DefaultLimit;
            o.DefaultTokenBudget = opts.DefaultTokenBudget;
        });
        builder.Services.AddSingleton<BanyanMemoryProvider>();
        builder.Services.AddSingleton<IMemoryNodeProvider>(sp => sp.GetRequiredService<BanyanMemoryProvider>());
        builder.Services.AddMemoryNode<BanyanMemoryProvider>(o =>
        {
            o.NodeId             = opts.NodeId;
            o.DisplayName        = opts.DisplayName;
            o.PathPrefix         = "/api/memory";
            o.RequireAuth        = opts.RequireAuth;
            o.DefaultLimit       = opts.DefaultLimit;
            o.MaxLimit           = opts.MaxLimit;
            o.DefaultTokenBudget = opts.DefaultTokenBudget;
            o.Schema             = BanyanMemoryProvider.BuildSchema();
        });

        // ── NID auth (Lite default = AnonymousAllowed; opt-in WritesRequired/AllRequired) ────
        builder.Services.AddSingleton(new NidAuthenticationOptions { Mode = opts.NidAuthMode });

        // ── Build pipeline ────────────────────────────────────────────────────
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        // Same gate as Banyan.Web: NID middleware only meaningful when a verifier is in DI.
        if (app.Services.GetService<NipIdentVerifier>() is not null)
            app.UseNidAuthentication();

        // Liveness + manifest are publicly readable.
        var version = typeof(MemoryNodeApp).Assembly.GetName().Version?.ToString() ?? "dev";
        app.MapGet("/api/health", () => Results.Ok(new { ok = true, role = "memory-node", version }));
        HealthEndpoints.Map(app);
        app.MapGet("/.nwm",      ([Microsoft.AspNetCore.Mvc.FromServices] BanyanNodeOptions o) =>
            Results.Json(BuildManifest(o, ca)));

        // NWP middleware claims the entire /api/memory/* subtree, so the schema endpoint lives
        // alongside the manifest (also referenced by /.nwm).
        app.MapGet("/.schema", () => Results.Json(BanyanMemoryProvider.BuildSchema()));

        // NPS-3 §8 NIP CA HTTP routes — mount when this node also acts as a CA.
        // Interoperates with the Go/Java NPS-CA references and our own RemoteNipCaClient.
        if (ca is not null)
            NipCaEndpoints.Map(app, mapHealth: false);

        app.UseMemoryNode<BanyanMemoryProvider>(o =>
        {
            o.NodeId             = opts.NodeId;
            o.DisplayName        = opts.DisplayName;
            o.PathPrefix         = "/api/memory";
            o.RequireAuth        = opts.RequireAuth;
            o.DefaultLimit       = opts.DefaultLimit;
            o.MaxLimit           = opts.MaxLimit;
            o.DefaultTokenBudget = opts.DefaultTokenBudget;
            o.Schema             = BanyanMemoryProvider.BuildSchema();
        });

        Console.WriteLine($"Banyan Memory Node listening on {opts.Urls}");
        Console.WriteLine($"  memory.db        : {memoryDb}");
        if (memoryStore.VecEnabled) Console.WriteLine("  sqlite-vec        : ANN enabled");
        if (ca is not null)         Console.WriteLine($"  CA (in-process)   : {ca.CaNid}");
        Console.WriteLine($"  trusted issuers  : {opts.TrustedIssuers.Count}");
        Console.WriteLine($"  require auth     : {opts.RequireAuth}");

        await app.RunAsync(ct);
    }

    private static NeuralWebManifest BuildManifest(BanyanNodeOptions opts, EmbeddedNipCa? ca) => new()
    {
        NodeId           = opts.NodeId,
        NodeType         = "memory",
        DisplayName      = opts.DisplayName,
        Nwp              = "1.0",
        PreferredFormat  = "application/nwp-frame",
        WireFormats      = new[] { "application/nwp-frame", "application/json" },
        TokenizerSupport = new[] { "approximate" },
        MinAssuranceLevel = "low",
        Endpoints = new NodeEndpoints
        {
            Schema = "/.schema",
            Query  = "/api/memory/query",
            Stream = "/api/memory/stream",
            Invoke = "",
        },
        Capabilities = new NodeCapabilities
        {
            Query           = true,
            Stream          = true,
            Subscribe       = false,
            VectorSearch    = true,
            ExtFrame        = false,
            TokenBudgetHint = true,
        },
        Auth = new NodeAuth
        {
            Required             = opts.RequireAuth,
            IdentityType         = "nip",
            TrustedIssuers       = opts.TrustedIssuers.Keys.ToArray(),
            RequiredCapabilities = Array.Empty<string>(),
            ScopeCheck           = "issuer",
            OcspUrl              = "",
        },
        DataSources    = new[] { "sqlite" + (ca is not null ? "+nip-ca" : "") },
        SchemaAnchors  = new Dictionary<string, string>(),
        Graph          = new NodeGraph { Refs = Array.Empty<NodeGraphRef>() },
    };

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
