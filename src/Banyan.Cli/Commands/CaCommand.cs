using Banyan.Auth;

namespace Banyan.Cli.Commands;

/// <summary>Sub-dispatcher for <c>banyan ca &lt;subcommand&gt;</c>.</summary>
internal static class CaCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return 64; }
        var sub = args[0];
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "init"   => await InitAsync(rest),
            "info"   => await InfoAsync(rest),
            "--help" or "-h" or "help" => Help(),
            _        => Unknown(sub),
        };
    }

    private static async Task<int> InitAsync(string[] args)
    {
        var opts = CaContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        opts.KeyPassphrase = CaContext.ResolvePassphrase(args) ?? "";
        if (string.IsNullOrEmpty(opts.KeyPassphrase))
        {
            Console.Error.WriteLine($"ca init: passphrase required (set ${CaContext.PassphraseEnvVar} or pass --passphrase).");
            return 2;
        }

        var keyPath = CommandContext.ExpandHome(opts.KeyFilePath);
        var force   = CommandContext.HasFlag(args, "--force");

        if (File.Exists(keyPath) && !force)
        {
            Console.WriteLine($"CA key already exists at {keyPath}. Use --force to regenerate.");
        }
        else
        {
            EmbeddedNipCa.GenerateKey(opts, overwrite: force);
            Console.WriteLine($"Generated Ed25519 CA key at {keyPath}");
        }

        await using var ca = await EmbeddedNipCa.OpenAsync(opts);
        Console.WriteLine($"Opened nipca.db at {CommandContext.ExpandHome(opts.DbPath)}");
        Console.WriteLine($"  CA NID:     {ca.CaNid}");
        Console.WriteLine($"  CA pub key: {ca.CaPubKey}");
        return 0;
    }

    private static async Task<int> InfoAsync(string[] args)
    {
        var opts = CaContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        opts.KeyPassphrase = CaContext.ResolvePassphrase(args) ?? "";
        if (string.IsNullOrEmpty(opts.KeyPassphrase))
        {
            Console.Error.WriteLine($"ca info: passphrase required (set ${CaContext.PassphraseEnvVar} or pass --passphrase).");
            return 2;
        }

        await using var ca = await EmbeddedNipCa.OpenAsync(opts);
        var all     = await ca.ListAsync(revokedOnly: false);
        var revoked = await ca.ListAsync(revokedOnly: true);
        Console.WriteLine($"CA NID:        {ca.CaNid}");
        Console.WriteLine($"CA pub key:    {ca.CaPubKey}");
        Console.WriteLine($"Issued certs:  {all.Count}");
        Console.WriteLine($"Revoked certs: {revoked.Count}");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            banyan ca <subcommand>
              init  Initialise the embedded NID CA (Ed25519 key + nipca.db)
                      --config PATH      (default: ~/.banyan/ca-config.json)
                      --passphrase P     (or env BANYAN_NIP_CA_PASSPHRASE; prompts otherwise)
                      --force            regenerate existing CA key
              info  Print CA NID, public key, issued/revoked counts
            """);
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"banyan ca: unknown subcommand '{sub}'. Run `banyan ca --help`.");
        return 64;
    }

    private static int PrintHelp() { Help(); return 64; }
}
