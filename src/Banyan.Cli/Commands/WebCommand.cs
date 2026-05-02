using Banyan.Web;

namespace Banyan.Cli.Commands;

internal static class WebCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (CommandContext.HasFlag(args, "--help") || CommandContext.HasFlag(args, "-h"))
        {
            Console.WriteLine("""
                banyan web — start the demo web UI

                  --urls URL          listen URLs (default: http://localhost:5180)
                  --memory-db PATH    SQLite memory.db path
                  --ca-db PATH        SQLite nipca.db path
                  --ca-key PATH       Ed25519 CA private key (PEM, encrypted)
                  --ca-nid NID        CA NID (default: urn:nps:ca:local.banyan:root)
                  --no-ca             skip opening the CA (memory-only mode)
                  --vec-lib PATH      sqlite-vec loadable extension (default: env BANYAN_SQLITE_VEC_LIB or ~/.banyan/sqlite-vec/vec0.so)
                  --nid-auth MODE     NID auth enforcement: AnonymousAllowed (default) | WritesRequired | AllRequired
                                      Requires CA loaded or --trusted-issuer configured
                  --trusted-issuer NID=PUBKEY
                                      Trust an external CA. Repeat for multiple CAs.
                                      PUBKEY format: ed25519:<base64>
                                      Example: --trusted-issuer urn:nps:ca:foo:root=ed25519:ABC...
                  --ocsp-url URL      OCSP endpoint of the external CA for revocation checks

                Auth:
                  Embedded CA: set BANYAN_NIP_CA_PASSPHRASE to unlock CA on startup.
                  External CA: use --trusted-issuer (no passphrase needed). --no-ca implied.
                  Without either, NID auth is disabled and /api/memory stays open.
                """);
            return 0;
        }

        var opts = new WebOptions();
        if (CommandContext.GetOption(args, "--urls")      is { } u) opts.Urls         = u;
        if (CommandContext.GetOption(args, "--memory-db") is { } m) opts.MemoryDbPath = m;
        if (CommandContext.GetOption(args, "--ca-db")     is { } d) opts.NipCaDbPath  = d;
        if (CommandContext.GetOption(args, "--ca-key")    is { } k) opts.NipCaKeyPath = k;
        if (CommandContext.GetOption(args, "--ca-nid")    is { } n) opts.CaNid        = n;
        if (CommandContext.GetOption(args, "--vec-lib")   is { } v) opts.SqliteVecLibPath = v;
        if (CommandContext.HasFlag(args, "--no-ca"))                opts.OpenCa       = false;
        if (CommandContext.GetOption(args, "--nid-auth")  is { } na &&
            Enum.TryParse<Banyan.Auth.NidAuthMode>(na, ignoreCase: true, out var mode)) opts.NidAuthMode = mode;
        if (CommandContext.GetOption(args, "--ocsp-url")  is { } ou) opts.ExternalOcspUrl = ou;

        // --trusted-issuer may repeat; each value is NID=PUBKEY
        foreach (var ti in CommandContext.GetOptions(args, "--trusted-issuer"))
        {
            var eq = ti.IndexOf('=');
            if (eq > 0) opts.TrustedIssuers[ti[..eq].Trim()] = ti[(eq + 1)..].Trim();
            else Console.Error.WriteLine($"[warn] --trusted-issuer ignored (expected NID=PUBKEY): {ti}");
        }

        await WebApp.RunAsync(opts, args);
        return 0;
    }
}
