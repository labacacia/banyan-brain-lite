using Banyan.Cli.Commands;

#if ONNX
// Full build (installer/dev): register the ONNX embedder so semantic embeddings
// are available. The slim CLI tool (packed with -p:SlimTool=true) omits the
// Banyan.Embedders.Onnx reference and this call, falling back to hashing — ONNX
// then comes from the Banyan.Embedders.Onnx NuGet package for library consumers.
Banyan.Embedders.OnnxEmbedderRegistration.Register();
#endif

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    PrintHelp();
    return 0;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

return command switch
{
    "keygen" => KeygenCommand.Run(rest),
    "init" => await InitCommand.RunAsync(rest),
    "reset-admin-pwd" => await ResetAdminPasswordCommand.RunAsync(rest),
    "login" => await LoginCommand.RunAsync(rest),
    "whoami" => WhoamiCommand.Run(rest),
    "logout" => await LogoutCommand.RunAsync(rest),
    "ca" => await CaCommand.RunAsync(rest),
    "agent" => await AgentCommand.RunAsync(rest),
    "web" => await WebCommand.RunAsync(rest),
    "mcp" => await McpCommand.RunAsync(rest),
    "pack" => await PackCommand.RunAsync(rest),
    "pool" => await PoolCommand.RunAsync(rest),
    "embedder" => await EmbedderCommand.RunAsync(rest),
    "serve" => await ServeCommand.RunAsync(rest),
    _ => Unknown(command),
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
          reset-admin-pwd
                    Reset an existing admin user's password
                      --config PATH
                      --admin-username U (default: admin)
                      --admin-password P
                      --confirm-password P
                      --no-confirm
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
          embedder profiles  List curated local embedder profiles
          embedder download  Pull a curated ONNX embedder + vocab and sqlite-vec
          embedder info      Show paths / sizes / load status

        MCP:
          mcp       Run as an MCP stdio server (Claude Desktop / Claude Code integration)
                      --db PATH          (default: ~/.banyan/memory.db)
                      --namespace NS     default write namespace (default: default)
                      --sqlite-vec PATH  sqlite-vec extension path

        Knowledge Packs:
          pack build PATH
                    Build a portable .banyanpack from .md, .txt, and .json files
                      --out PATH
                      --pack-id ID
                      --name NAME
                      --version VERSION
                      --dry-run
          pack inspect PATH
                    Inspect a .banyanpack manifest
          pack mount PATH
                    Mount a .banyanpack into a namespace
                      --namespace NS
          pack list
                    List mounted knowledge packs
                      --namespace NS
          pack unmount ID
                    Unmount a pack id from a namespace
                      --namespace NS

        Shared Memory Pools:
          pool create NAME
                    Create a local shared pool
                      --scope personal|workspace|agent
                      --owner ID
                      --db PATH
          pool list
                    List local shared pools
          pool add-member POOL MEMBER
                    Add a member and bind that agent to the pool
          pool remove-member POOL MEMBER
                    Remove a member from a pool

        Demo:
          web       Start the demo web UI (default: http://localhost:5180)

        Memory Node (P3):
          serve     Start a Memory Node (NWP middleware on http://localhost:17433)
        """);
}
