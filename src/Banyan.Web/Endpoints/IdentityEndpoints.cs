using System.Text;
using System.Text.Json;

namespace Banyan.Web.Endpoints;

public static class IdentityEndpoints
{
    public sealed record MeResponse(bool LoggedIn, string? Subject, string? Name, string[] Scopes, DateTimeOffset? ExpiresAt, string? Issuer, string? ClientId);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/identity/me", (WebOptions opts) =>
        {
            var path = WebOptions.ExpandHome(opts.TokensCachePath);
            if (!File.Exists(path))
                return Results.Ok(new MeResponse(false, null, null, Array.Empty<string>(), null, null, null));

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var access = root.GetProperty("AccessToken").GetString() ?? "";

            // Decode the JWT payload (no signature verification — demo only, token is from a local file we trust).
            if (DecodeJwtPayload(access) is not { } claims)
                return Results.Ok(new MeResponse(false, null, null, Array.Empty<string>(), null, null, null));

            string? Get(string k) => claims.TryGetProperty(k, out var v) ? v.GetString() : null;
            string[] Scopes()
            {
                if (!claims.TryGetProperty("scope", out var s)) return Array.Empty<string>();
                return s.ValueKind switch
                {
                    JsonValueKind.String => (s.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    JsonValueKind.Array  => s.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray(),
                    _ => Array.Empty<string>()
                };
            }

            return Results.Ok(new MeResponse(
                LoggedIn:  true,
                Subject:   Get("sub"),
                Name:      Get("name") ?? Get("preferred_username"),
                Scopes:    Scopes(),
                ExpiresAt: root.TryGetProperty("ExpiresAt", out var exp) && DateTimeOffset.TryParse(exp.GetString(), out var dt) ? dt : null,
                Issuer:    root.TryGetProperty("Issuer", out var iss) ? iss.GetString() : null,
                ClientId:  root.TryGetProperty("ClientId", out var cid) ? cid.GetString() : null));
        }).WithTags("identity");
    }

    private static JsonElement? DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var pad = parts[1].Length % 4 == 0 ? "" : new string('=', 4 - parts[1].Length % 4);
            var b64 = parts[1].Replace('-', '+').Replace('_', '/') + pad;
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch { return null; }
    }
}
