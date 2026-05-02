using Banyan.Auth;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace Banyan.Cli.Commands;

/// <summary>Sub-dispatcher for <c>banyan agent &lt;subcommand&gt;</c>.</summary>
internal static class AgentCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return 64; }
        var sub  = args[0];
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "issue"  => await IssueAsync(rest),
            "list"   => await ListAsync(rest),
            "revoke" => await RevokeAsync(rest),
            "verify" => await VerifyAsync(rest),
            "--help" or "-h" or "help" => Help(),
            _        => Unknown(sub),
        };
    }

    // ── issue ────────────────────────────────────────────────────────────────

    private static async Task<int> IssueAsync(string[] args)
    {
        var id = CommandContext.GetOption(args, "--id");
        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("agent issue: --id is required.");
            return 2;
        }
        var capsCsv = CommandContext.GetOption(args, "--cap") ?? "";
        var caps    = capsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyOut  = CommandContext.GetOption(args, "--key-out");

        // Generate the agent's Ed25519 keypair locally; the CA only sees the public half.
        var algo = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var pubKey  = NipSigner.EncodePublicKey(key.PublicKey);
        var privRaw = key.Export(KeyBlobFormat.RawPrivateKey);

        string nid, serial, issuedBy, expiresAt;
        if (RemoteUrl(args) is { } remoteUrl)
        {
            using var rc = new RemoteNipCaClient(remoteUrl);
            var resp = await rc.RegisterAgentAsync(id, pubKey, caps);
            nid = resp.Nid; serial = resp.Serial; expiresAt = resp.ExpiresAt;
            issuedBy = (await rc.CaCertAsync())?.Nid ?? "(remote)";
        }
        else
        {
            await using var ca = await OpenCaOrFail(args);
            if (ca is null) return 2;
            var frame = await ca.RegisterAgentAsync(id, pubKey, caps);
            nid = frame.Nid; serial = frame.Serial; issuedBy = frame.IssuedBy; expiresAt = frame.ExpiresAt;
        }

        if (!string.IsNullOrEmpty(keyOut))
        {
            var path = CommandContext.ExpandHome(keyOut);
            var dir  = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, privRaw);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Console.WriteLine($"Wrote agent private key (raw 32 bytes) to {path}");
        }
        else
        {
            Console.WriteLine("Agent private key (base64 raw 32 bytes — store securely, will not be shown again):");
            Console.WriteLine($"  {Convert.ToBase64String(privRaw)}");
        }

        Console.WriteLine();
        Console.WriteLine("Issued agent NID:");
        Console.WriteLine($"  nid:        {nid}");
        Console.WriteLine($"  serial:     {serial}");
        Console.WriteLine($"  issued by:  {issuedBy}");
        Console.WriteLine($"  expires at: {expiresAt}");
        Console.WriteLine($"  capabilities: {(caps.Length == 0 ? "(none)" : string.Join(", ", caps))}");
        return 0;
    }

    private static string? RemoteUrl(string[] args)
        => CommandContext.GetOption(args, "--remote")
        ?? Environment.GetEnvironmentVariable("BANYAN_CA_URL");

    // ── list ─────────────────────────────────────────────────────────────────

    private static async Task<int> ListAsync(string[] args)
    {
        await using var ca = await OpenCaOrFail(args);
        if (ca is null) return 2;

        var revokedOnly = CommandContext.HasFlag(args, "--revoked");
        var rows = await ca.ListAsync(revokedOnly);
        if (rows.Count == 0)
        {
            Console.WriteLine(revokedOnly ? "No revoked certs." : "No certs issued yet.");
            return 0;
        }

        Console.WriteLine($"{"NID",-50} {"SERIAL",-18} {"TYPE",-8} {"ISSUED",-22} {"STATUS"}");
        foreach (var r in rows)
        {
            var status = r.RevokedAt.HasValue
                ? $"revoked: {r.RevokeReason}"
                : (r.ExpiresAt < DateTime.UtcNow ? "expired" : "active");
            Console.WriteLine($"{r.Nid,-50} {r.Serial,-18} {r.EntityType,-8} {r.IssuedAt:yyyy-MM-ddTHH:mm:ssZ} {status}");
        }
        return 0;
    }

    // ── revoke ───────────────────────────────────────────────────────────────

    private static async Task<int> RevokeAsync(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("agent revoke: positional <nid> argument required.");
            return 2;
        }
        var nid    = args[0];
        var rest   = args.Skip(1).ToArray();
        var reason = CommandContext.GetOption(rest, "--reason") ?? "operator-initiated";

        if (RemoteUrl(rest) is { } remoteUrl)
        {
            using var rc = new RemoteNipCaClient(remoteUrl);
            var r = await rc.RevokeAsync(nid, reason);
            Console.WriteLine($"Revoked {r.Nid}");
            Console.WriteLine($"  reason:    {r.Reason}");
            Console.WriteLine($"  signed at: {r.RevokedAt}");
            return 0;
        }

        await using var ca = await OpenCaOrFail(rest);
        if (ca is null) return 2;
        var frame = await ca.RevokeAsync(nid, reason);
        Console.WriteLine($"Revoked {nid}");
        Console.WriteLine($"  reason:    {reason}");
        Console.WriteLine($"  signed at: {frame.RevokedAt}");
        return 0;
    }

    // ── verify ───────────────────────────────────────────────────────────────

    private static async Task<int> VerifyAsync(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("agent verify: positional <nid> argument required.");
            return 2;
        }
        var nid  = args[0];
        var rest = args.Skip(1).ToArray();

        if (RemoteUrl(rest) is { } remoteUrl)
        {
            using var rc = new RemoteNipCaClient(remoteUrl);
            var v = await rc.VerifyAsync(nid);
            if (v.Valid)
            {
                Console.WriteLine($"VALID  {nid}");
                Console.WriteLine($"  serial:  {v.Serial}");
                Console.WriteLine($"  expires: {v.ExpiresAt}");
                return 0;
            }
            Console.WriteLine($"INVALID {nid}");
            Console.WriteLine($"  error: {v.ErrorCode}");
            Console.WriteLine($"  msg:   {v.Message}");
            return 1;
        }

        await using var ca = await OpenCaOrFail(rest);
        if (ca is null) return 2;
        var result = await ca.VerifyAsync(nid);
        if (result.Valid)
        {
            Console.WriteLine($"VALID  {nid}");
            Console.WriteLine($"  serial:    {result.Record!.Serial}");
            Console.WriteLine($"  expires:   {result.Record.ExpiresAt:O}");
            return 0;
        }
        Console.WriteLine($"INVALID {nid}");
        Console.WriteLine($"  error: {result.ErrorCode}");
        Console.WriteLine($"  msg:   {result.Message}");
        return 1;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<EmbeddedNipCa?> OpenCaOrFail(string[] args)
    {
        var opts = CaContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        opts.KeyPassphrase = CaContext.ResolvePassphrase(args) ?? "";
        if (string.IsNullOrEmpty(opts.KeyPassphrase))
        {
            Console.Error.WriteLine($"agent: CA passphrase required (set ${CaContext.PassphraseEnvVar} or pass --passphrase).");
            return null;
        }

        try { return await EmbeddedNipCa.OpenAsync(opts); }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"agent: {ex.Message}");
            return null;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            banyan agent <subcommand>
              issue   Issue a new agent NID certificate. Generates the agent's keypair locally.
                        --id ID            agent identifier (required)
                        --cap a,b,c        comma-separated capabilities
                        --key-out PATH     write private key to file (default: print to stdout)
                        --remote URL       talk to a remote NPS-CA (or env BANYAN_CA_URL); skips local CA
                        --passphrase P     CA key passphrase (or env BANYAN_NIP_CA_PASSPHRASE; local mode)
              list    List all issued certs (local mode only — NPS spec doesn't expose list)
                        --revoked          show only revoked certs
              revoke  <nid> --reason "..." [--remote URL]
              verify  <nid>                [--remote URL]
            """);
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"banyan agent: unknown subcommand '{sub}'. Run `banyan agent --help`.");
        return 64;
    }

    private static int PrintHelp() { Help(); return 64; }
}
