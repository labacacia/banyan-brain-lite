using Banyan.Node;

var opts = new BanyanNodeOptions();
string? Get(string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}
if (Get("--urls")      is { } u)  opts.Urls         = u;
if (Get("--memory-db") is { } m)  opts.MemoryDbPath = m;
if (Get("--ca-db")     is { } d)  opts.NipCaDbPath  = d;
if (Get("--ca-key")    is { } k)  opts.NipCaKeyPath = k;
if (Get("--ca-nid")    is { } n)  opts.CaNid        = n;
if (Get("--vec-lib")   is { } v)  opts.SqliteVecLibPath = v;
if (Get("--node-id")   is { } id) opts.NodeId       = id;
if (args.Contains("--allow-anon")) opts.RequireAuth = false;

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--trusted-issuer")
    {
        var parts = args[i + 1].Split('=', 2);
        if (parts.Length == 2) opts.TrustedIssuers[parts[0]] = parts[1];
    }
}

await MemoryNodeApp.RunAsync(opts, args);
