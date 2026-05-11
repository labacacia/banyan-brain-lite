using Banyan.Web;

var opts = new WebOptions();
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
if (Get("--vec-lib")   is { } vl) opts.SqliteVecLibPath = vl;
if (args.Contains("--no-ca"))     opts.OpenCa       = false;

await WebApp.RunAsync(opts, args);
