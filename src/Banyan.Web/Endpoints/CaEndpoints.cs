using Banyan.Auth;
using Microsoft.AspNetCore.Mvc;
using NPS.NIP.Verification;
using System.Text.Json;

namespace Banyan.Web.Endpoints;

public static class CaEndpoints
{
    public sealed record CaInfo(string CaNid, string CaPubKey, int? IssuedCount, int? RevokedCount, string Mode);

    public static void Map(WebApplication app, bool requireAdmin)
    {
        var ep = app.MapGet("/api/ca", async (HttpContext ctx, [FromServices] EmbeddedNipCa? ca, [FromServices] NipVerifierOptions? verifierOpts, IHttpClientFactory httpFactory) =>
        {
            // ── Embedded CA (full stats) ──────────────────────────────────────────────
            if (ca is not null)
            {
                var all     = await ca.ListAsync(revokedOnly: false);
                var revoked = await ca.ListAsync(revokedOnly: true);
                return Results.Ok(new CaInfo(ca.CaNid, ca.CaPubKey, all.Count, revoked.Count, "embedded"));
            }

            // ── External CA (reads discovery doc for display) ─────────────────────────
            if (verifierOpts?.TrustedIssuers is { Count: > 0 } issuers)
            {
                var nid    = issuers.Keys.First();
                var pubKey = issuers[nid];

                // Try to fetch live stats from the external CA's discovery endpoint.
                // The external CA NID encodes its base URL via NPS-3 convention
                // e.g. NIPCA__BASEURL in docker-compose. We don't know the URL here,
                // so we return what we have from the trust anchor and leave counts null.
                return Results.Ok(new CaInfo(nid, pubKey, null, null, "external"));
            }

            return Results.NotFound(new { error = "No CA configured — start with BANYAN_NIP_CA_PASSPHRASE or --trusted-issuer" });
        }).WithTags("ca");
        if (requireAdmin) ep.RequireAuthorization("admin");
    }
}
