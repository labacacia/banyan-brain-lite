using Banyan.Cli.Commands;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return 0;
}

var command  = args[0];
var rest     = args.Skip(1).ToArray();

return command switch
{
    "keygen" => KeygenCommand.Run(rest),
    "init"   => await InitCommand.RunAsync(rest),
    "login"  => await LoginCommand.RunAsync(rest),
    "whoami" => WhoamiCommand.Run(rest),
    "logout" => await LogoutCommand.RunAsync(rest),
    "ca"     => await CaCommand.RunAsync(rest),
    "agent"  => await AgentCommand.RunAsync(rest),
    "web"    => await WebCommand.RunAsync(rest),
    "embedder" => await EmbedderCommand.RunAsync(rest),
    "serve"    => await ServeCommand.RunAsync(rest),
    _        => Unknown(command),
};

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"banyan: unknown command '{cmd}'. Run `banyan --help` for usage.");
    return 64;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Banyan Memory Node — CLI

        Usage: banyan <command> [options]

        Identity (P1.5b):
          keygen    Generate an RSA private key (PEM) for JWT/OIDC signing
                      --out PATH         (default: ~/.banyan/identity-signing.pem)
                      --bits N           (default: 2048)
                      --force            overwrite existing
          init      Initialise identity.db, signing key, CLI OIDC client, admin user
                      --config PATH      (default: ~/.banyan/identity-config.json)
                      --admin-username U
                      --admin-password P
          login     Log in via OIDC Device Code (default) or PKCE (--browser)
                      --browser
                      --issuer URL
                      --client-id ID
          whoami    Show subject + scopes from cached access token
          logout    Revoke refresh token at issuer and clear local cache

        NID CA (P1.5a):
          ca init   Initialise the embedded NID CA (Ed25519 key + nipca.db)
          ca info   Print CA NID, public key, issued/revoked counts
          agent issue   Issue a new agent NID certificate
          agent list    List issued certs
          agent revoke  Revoke a cert by NID
          agent verify  Verify a cert by NID

        Embedder (P2.1):
          embedder download  Pull multilingual-e5-small ONNX + SentencePiece (~123 MB)
          embedder info      Show paths / sizes / load status

        Demo:
          web       Start the demo web UI (default: http://localhost:5180)

        Memory Node (P3):
          serve     Start a Memory Node (NWP middleware on http://localhost:17433)
        """);
}
