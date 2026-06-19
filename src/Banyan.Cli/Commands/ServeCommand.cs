// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Node;

namespace Banyan.Cli.Commands;

internal static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (CommandContext.HasFlag(args, "--help") || CommandContext.HasFlag(args, "-h"))
        {
            Console.WriteLine("""
                banyan serve — start a Banyan Node (Memory + Act + MCP on one port)

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
                  --no-act                  disable the NWP Act Node at /api/act
                  --no-mcp                  disable the HTTP MCP endpoint
                  --mcp-path PATH           MCP endpoint path (default: /mcp)
                  --mcp-namespace NS        default namespace for the MCP remember tool (default: default)
                  --auth-mode MODE          offline (default) | hub
                                            offline: all requests accepted without credentials
                                            hub:     Hub OIDC JWT + Hub NID CA required
                  --hub-url URL             Hub OIDC authority URL (required for --auth-mode hub)
                  --hub-audience AUD        Expected JWT audience in Hub tokens (default: banyan)
                  --hub-nid-issuer NID=KEY  Hub NIP CA trust anchor (NID=ed25519:KEY format)

                Protocol surfaces (all on one port by default):
                  /api/memory   NWP Memory Node  — NPS-3 §5 recall/stream
                  /api/act      NWP Act Node     — NPS-2 §7 memory.recall/remember/update/forget
                  /mcp          MCP HTTP (SSE)   — Claude Desktop / Claude Code via HTTP

                Claude Desktop config (~/.../claude_desktop_config.json):
                  { "mcpServers": { "banyan": { "url": "http://localhost:17433/mcp" } } }

                Auth:
                  Clients submit IdentFrame as per the NWP spec (X-NWP-Agent header / per-frame).
                  /api/health and /.nwm are publicly readable.
                  When --allow-anon is set, the NWP middleware skips IdentFrame verification.
                """);
            return 0;
        }

        var opts = new BanyanNodeOptions();
        if (CommandContext.GetOption(args, "--urls")          is { } u)  opts.Urls              = u;
        if (CommandContext.GetOption(args, "--memory-db")     is { } m)  opts.MemoryDbPath      = m;
        if (CommandContext.GetOption(args, "--vec-lib")       is { } v)  opts.SqliteVecLibPath  = v;
        if (CommandContext.GetOption(args, "--ca-db")         is { } d)  opts.NipCaDbPath       = d;
        if (CommandContext.GetOption(args, "--ca-key")        is { } k)  opts.NipCaKeyPath      = k;
        if (CommandContext.GetOption(args, "--ca-nid")        is { } n)  opts.CaNid             = n;
        if (CommandContext.GetOption(args, "--node-id")       is { } id) opts.NodeId            = id;
        if (CommandContext.GetOption(args, "--mcp-path")      is { } mp) opts.McpPath           = mp;
        if (CommandContext.GetOption(args, "--mcp-namespace") is { } mn) opts.McpDefaultNamespace = mn;
        if (CommandContext.HasFlag(args, "--allow-anon")) opts.RequireAuth  = false;
        if (CommandContext.HasFlag(args, "--no-act"))     opts.EnableActNode = false;
        if (CommandContext.HasFlag(args, "--no-mcp"))     opts.EnableMcp     = false;
        if (CommandContext.GetOption(args, "--nid-auth")  is { } na &&
            Enum.TryParse<NidAuthMode>(na, ignoreCase: true, out var mode)) opts.NidAuthMode = mode;
        if (CommandContext.GetOption(args, "--auth-mode") is { } am &&
            Enum.TryParse<BanyanAuthMode>(am, ignoreCase: true, out var authMode)) opts.AuthMode = authMode;
        if (CommandContext.GetOption(args, "--hub-url")      is { } hu)  opts.Hub.JwtAuthority = hu;
        if (CommandContext.GetOption(args, "--hub-audience") is { } hau) opts.Hub.JwtAudience  = hau;
        if (CommandContext.GetOption(args, "--hub-nid-issuer") is { } hni)
        {
            var eq = hni.IndexOf('=');
            if (eq > 0) { opts.Hub.NidIssuerNid = hni[..eq].Trim(); opts.Hub.NidPublicKey = hni[(eq + 1)..].Trim(); }
            else Console.Error.WriteLine($"[warn] --hub-nid-issuer ignored (expected NID=PUBKEY): {hni}");
        }

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
