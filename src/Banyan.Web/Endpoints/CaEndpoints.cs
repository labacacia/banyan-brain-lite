// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Microsoft.AspNetCore.Mvc;
using NPS.NIP.Verification;
using System.Text.Json;

namespace Banyan.Web.Endpoints;

public static class CaEndpoints
{
    public sealed record CaInfo(string CaNid, string CaPubKey, int? IssuedCount, int? RevokedCount, string Mode, string? ServerAddress = null);
    public sealed record CaServerConfigResponse(string Type, string? Address, string? CaNid, string? CaPubKey, bool RuntimeActive);
    public sealed record CaServerConfigBody(string Type, string? Address);
    public sealed record CaServerConfigSaveResponse(bool Saved, bool RuntimeActive, bool RestartRequired, CaServerProbeResult Probe);

    public static void Map(WebApplication app, bool requireAdmin)
    {
        var ep = app.MapGet("/api/ca", async (HttpContext ctx, [FromServices] EmbeddedNipCa? ca, [FromServices] NipVerifierOptions? verifierOpts, [FromServices] IHttpClientFactory httpFactory) =>
        {
            var opts = ctx.RequestServices.GetService<WebOptions>() ?? new WebOptions();

            // ── Embedded CA (full stats) ──────────────────────────────────────────────
            if (ca is not null && opts.CaServerType == CaServerMode.Embedded)
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
                return Results.Ok(new CaInfo(nid, pubKey, null, null, "external", opts?.ExternalCaServerAddress));
            }

            return Results.NotFound(new { error = "No CA configured — start with BANYAN_NIP_CA_PASSPHRASE or --trusted-issuer" });
        }).WithTags("ca");
        if (requireAdmin) ep.RequireAuthorization("admin");

        var group = app.MapGroup("/api/ca/server").WithTags("ca");
        if (requireAdmin) group.RequireAuthorization("admin");

        group.MapGet("/", (HttpContext ctx, [FromServices] NipVerifierOptions? verifierOpts) =>
        {
            var opts = ctx.RequestServices.GetService<WebOptions>() ?? new WebOptions();
            var issuer = verifierOpts?.TrustedIssuers.FirstOrDefault();
            var hasIssuer = issuer.HasValue && !string.IsNullOrWhiteSpace(issuer.Value.Key);
            return Results.Ok(new CaServerConfigResponse(
                opts.CaServerType.ToString().ToLowerInvariant(),
                opts.ExternalCaServerAddress,
                hasIssuer ? issuer!.Value.Key : null,
                hasIssuer ? issuer!.Value.Value : null,
                verifierOpts is not null));
        });

        group.MapPost("/test", async (CaServerConfigBody body, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (!TryParseMode(body.Type, out var mode))
                return Results.BadRequest(new { error = "ca server type must be embedded or external" });

            if (mode == CaServerMode.Embedded)
                return Results.Ok(new CaServerProbeResult(true, null, null, null, "Embedded CA", "Embedded CA selected."));

            var probe = await CaServerProbe.TestExternalAsync(body.Address, httpFactory.CreateClient(), ct);
            return probe.Ok ? Results.Ok(probe) : Results.BadRequest(probe);
        });

        group.MapPut("/", async (
            CaServerConfigBody body,
            [FromServices] WebOptions opts,
            IHttpClientFactory httpFactory,
            [FromServices] NipVerifierOptions? verifierOpts,
            CancellationToken ct) =>
        {
            if (!TryParseMode(body.Type, out var mode))
                return Results.BadRequest(new { error = "ca server type must be embedded or external" });

            if (mode == CaServerMode.Embedded)
            {
                opts.CaServerType = CaServerMode.Embedded;
                opts.ExternalCaServerAddress = null;
                opts.TrustedIssuers.Clear();
                opts.OpenCa = true;
                WebConfigStore.Save(opts);
                var probe = new CaServerProbeResult(true, null, null, null, "Embedded CA", "Embedded CA selected.");
                return Results.Ok(new CaServerConfigSaveResponse(true, verifierOpts is not null, verifierOpts is null, probe));
            }

            var test = await CaServerProbe.TestExternalAsync(body.Address, httpFactory.CreateClient(), ct);
            if (!test.Ok || test.CaNid is null || test.PublicKey is null)
                return Results.BadRequest(new CaServerConfigSaveResponse(false, false, false, test));

            opts.CaServerType = CaServerMode.External;
            opts.OpenCa = false;
            opts.ExternalCaServerAddress = test.Address;
            opts.TrustedIssuers.Clear();
            opts.TrustedIssuers[test.CaNid] = test.PublicKey;
            if (verifierOpts is not null)
            {
                verifierOpts.TrustedIssuers.Clear();
                verifierOpts.TrustedIssuers[test.CaNid] = test.PublicKey;
            }

            WebConfigStore.Save(opts);
            return Results.Ok(new CaServerConfigSaveResponse(
                Saved: true,
                RuntimeActive: verifierOpts is not null,
                RestartRequired: verifierOpts is null,
                Probe: test));
        });
    }

    private static bool TryParseMode(string? value, out CaServerMode mode)
    {
        if (Enum.TryParse(value, ignoreCase: true, out mode))
            return true;

        mode = CaServerMode.Embedded;
        return false;
    }
}
