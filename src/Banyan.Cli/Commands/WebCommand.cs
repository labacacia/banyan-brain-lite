// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
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
                  --ca-server-type TYPE
                                      embedded (default) | external. Persists to ~/.banyan/web-config.json
                  --ca-server-address URL
                                      Required when --ca-server-type external. Tested before saving.
                  --no-ca             skip opening the CA (memory-only mode)
                  --vec-lib PATH      sqlite-vec loadable extension (default: env BANYAN_SQLITE_VEC_LIB or ~/.banyan/sqlite-vec/vec0.so)
                  --nid-auth MODE     NID auth enforcement: AnonymousAllowed (default) | WritesRequired | AllRequired
                                      Requires CA loaded or --trusted-issuer configured
                  --trusted-issuer NID=PUBKEY
                                      Trust an external CA. Repeat for multiple CAs.
                                      PUBKEY format: ed25519:<base64>
                                      Example: --trusted-issuer urn:nps:ca:foo:root=ed25519:ABC...
                  --ocsp-url URL      OCSP endpoint of the external CA for revocation checks
                  --auth-mode MODE    local (default) | hub
                                      local: built-in admin account + embedded identity server
                                      hub:   Hub OIDC issues JWTs; Hub NID CA for agents
                  --hub-url URL       Hub OIDC authority URL (required for --auth-mode hub)
                  --hub-audience AUD  Expected JWT audience in Hub tokens (default: banyan)
                  --hub-nid-issuer NID=PUBKEY
                                      Hub NIP CA trust anchor (NID=ed25519:KEY format)

                Auth:
                  Local mode: Lite auto-initialises a local CA and admin account on first launch.
                  Hub mode:   No local admin — Hub OIDC authority validates all JWTs.
                  External CA: use --ca-server-type external --ca-server-address URL.
                """);
            return 0;
        }

        var opts = WebConfigStore.Load();
        if (CommandContext.GetOption(args, "--urls")      is { } u) opts.Urls         = u;
        if (CommandContext.GetOption(args, "--memory-db") is { } m) opts.MemoryDbPath = m;
        if (CommandContext.GetOption(args, "--ca-db")     is { } d) opts.NipCaDbPath  = d;
        if (CommandContext.GetOption(args, "--ca-key")    is { } k) opts.NipCaKeyPath = k;
        if (CommandContext.GetOption(args, "--ca-nid")    is { } n) opts.CaNid        = n;
        if (CommandContext.GetOption(args, "--ca-server-address") is { } csa) opts.ExternalCaServerAddress = csa;
        if (CommandContext.GetOption(args, "--vec-lib")   is { } v) opts.SqliteVecLibPath = v;
        if (CommandContext.HasFlag(args, "--no-ca"))                opts.OpenCa       = false;
        if (CommandContext.GetOption(args, "--nid-auth")  is { } na &&
            Enum.TryParse<Banyan.Auth.NidAuthMode>(na, ignoreCase: true, out var mode)) opts.NidAuthMode = mode;
        if (CommandContext.GetOption(args, "--ocsp-url")  is { } ou) opts.ExternalOcspUrl = ou;
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

        // --trusted-issuer may repeat; each value is NID=PUBKEY
        foreach (var ti in CommandContext.GetOptions(args, "--trusted-issuer"))
        {
            var eq = ti.IndexOf('=');
            if (eq > 0) opts.TrustedIssuers[ti[..eq].Trim()] = ti[(eq + 1)..].Trim();
            else Console.Error.WriteLine($"[warn] --trusted-issuer ignored (expected NID=PUBKEY): {ti}");
        }

        if (CommandContext.GetOption(args, "--ca-server-type") is { } cst)
        {
            if (!Enum.TryParse<CaServerMode>(cst, ignoreCase: true, out var caMode))
            {
                Console.Error.WriteLine("web: --ca-server-type must be embedded or external");
                return 64;
            }

            opts.CaServerType = caMode;
            if (caMode == CaServerMode.External)
            {
                if (string.IsNullOrWhiteSpace(opts.ExternalCaServerAddress))
                {
                    Console.Error.WriteLine("web: --ca-server-address is required when --ca-server-type external");
                    return 64;
                }

                var probe = await CaServerProbe.TestExternalAsync(opts.ExternalCaServerAddress);
                if (!probe.Ok || probe.CaNid is null || probe.PublicKey is null)
                {
                    Console.Error.WriteLine($"web: external CA server test failed; configuration not saved. {probe.Message}");
                    return 2;
                }

                opts.OpenCa = false;
                opts.ExternalCaServerAddress = probe.Address;
                opts.TrustedIssuers.Clear();
                opts.TrustedIssuers[probe.CaNid] = probe.PublicKey;
                WebConfigStore.Save(opts);
                Console.WriteLine($"Configured external CA server {probe.Address} ({probe.CaNid})");
            }
            else
            {
                opts.OpenCa = true;
                opts.ExternalCaServerAddress = null;
                opts.TrustedIssuers.Clear();
                WebConfigStore.Save(opts);
                Console.WriteLine("Configured embedded CA server.");
            }
        }

        await WebApp.RunAsync(opts, args);
        return 0;
    }
}
