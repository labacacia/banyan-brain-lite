using Banyan.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Banyan.Web.Endpoints;

public static class CaEndpoints
{
    public sealed record CaInfo(string CaNid, string CaPubKey, int IssuedCount, int RevokedCount);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/ca", async ([FromServices] EmbeddedNipCa? ca) =>
        {
            if (ca is null) return Results.NotFound(new { error = "CA not loaded — start the server with BANYAN_NIP_CA_PASSPHRASE" });
            var all     = await ca.ListAsync(revokedOnly: false);
            var revoked = await ca.ListAsync(revokedOnly: true);
            return Results.Ok(new CaInfo(ca.CaNid, ca.CaPubKey, all.Count, revoked.Count));
        }).WithTags("ca");
    }
}
