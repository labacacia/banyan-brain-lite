using Banyan.Node;

namespace Banyan.Cli.Commands;

internal static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (CommandContext.HasFlag(args, "--help") || CommandContext.HasFlag(args, "-h"))
        {
            Console.WriteLine("""
                banyan serve — start a Memory Node (HTTP NWP surrogate)

                  --urls URL                listen URLs (default: http://localhost:17433)
                  --memory-db PATH          SQLite memory.db path
                  --vec-lib PATH            sqlite-vec extension override
                  --ca-db PATH              embedded NIP CA database
                  --ca-key PATH             CA private-key PEM (passphrase via BANYAN_NIP_CA_PASSPHRASE)
                  --ca-nid NID              CA NID (default: urn:nps:ca:local.banyan:root)
                  --allow-anon              disable NID auth on the NWP middleware (read-only demo)
                  --node-id ID              advertised node id in /.nwm (default: banyan-memory-node)
                  --nid-auth MODE           NID auth enforcement: AnonymousAllowed (default)
                                            | WritesRequired | AllRequired
                  --trusted-issuer NID=PUB  add a trusted CA issuer (repeatable)

                Auth:
                  Clients submit IdentFrame as per the NWP spec (X-NWP-Agent header / per-frame).
                  /api/health and /.nwm are publicly readable.
                  When --allow-anon is set, the NWP middleware skips IdentFrame verification.
                """);
            return 0;
        }

        var opts = new BanyanNodeOptions();
        if (CommandContext.GetOption(args, "--urls")      is { } u) opts.Urls         = u;
        if (CommandContext.GetOption(args, "--memory-db") is { } m) opts.MemoryDbPath = m;
        if (CommandContext.GetOption(args, "--vec-lib")   is { } v) opts.SqliteVecLibPath = v;
        if (CommandContext.GetOption(args, "--ca-db")     is { } d) opts.NipCaDbPath  = d;
        if (CommandContext.GetOption(args, "--ca-key")    is { } k) opts.NipCaKeyPath = k;
        if (CommandContext.GetOption(args, "--ca-nid")    is { } n) opts.CaNid        = n;
        if (CommandContext.GetOption(args, "--node-id")   is { } id) opts.NodeId       = id;
        if (CommandContext.HasFlag(args, "--allow-anon")) opts.RequireAuth = false;
        if (CommandContext.GetOption(args, "--nid-auth")  is { } na &&
            Enum.TryParse<Banyan.Auth.NidAuthMode>(na, ignoreCase: true, out var mode)) opts.NidAuthMode = mode;

        // Repeatable --trusted-issuer NID=PUB
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--trusted-issuer")
            {
                var parts = args[i + 1].Split('=', 2);
                if (parts.Length == 2) opts.TrustedIssuers[parts[0]] = parts[1];
            }
        }

        await MemoryNodeApp.RunAsync(opts, args);
        return 0;
    }
}
