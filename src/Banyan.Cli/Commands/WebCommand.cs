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

                Auth:
                  Set BANYAN_NIP_CA_PASSPHRASE in the environment to unlock the CA on startup.
                  Without it, /api/agents and /api/ca return 404 but /api/memory still works.
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

        await WebApp.RunAsync(opts, args);
        return 0;
    }
}
