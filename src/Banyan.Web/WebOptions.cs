namespace Banyan.Web;

/// <summary>
/// Configuration for the demo web app. All paths support a leading <c>~</c>.
/// CA passphrase comes from <c>BANYAN_NIP_CA_PASSPHRASE</c> at startup; not stored here.
/// </summary>
public sealed class WebOptions
{
    public string  Urls           { get; set; } = "http://localhost:5180";
    public string  MemoryDbPath   { get; set; } = "~/.banyan/memory.db";
    public string  NipCaDbPath    { get; set; } = "~/.banyan/nipca.db";
    public string  NipCaKeyPath   { get; set; } = "~/.banyan/nipca-key.pem";
    public string  CaNid          { get; set; } = "urn:nps:ca:local.banyan:root";
    public string  TokensCachePath{ get; set; } = "~/.banyan/tokens.json";
    public bool    OpenCa         { get; set; } = true;

    /// <summary>Path to the sqlite-vec loadable extension. Null = auto-discover (env var / default cache).</summary>
    public string? SqliteVecLibPath { get; set; }

    /// <summary>How aggressively to enforce IdentFrame auth on /api/* and /v1/* routes. Default: AnonymousAllowed (Lite demo).</summary>
    public Banyan.Auth.NidAuthMode NidAuthMode { get; set; } = Banyan.Auth.NidAuthMode.AnonymousAllowed;

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
