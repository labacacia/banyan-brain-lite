// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Microsoft.Extensions.DependencyInjection;
using NPS.NIP.Http;

namespace Banyan.Node.Auth;

/// <summary>
/// Mounts the NIP CA HTTP API (NPS-3 §6–8). Delegates to the SDK's
/// <see cref="NipCaRouter.MapNipCa"/> (<c>NPS.NIP</c>) instead of the previous hand-rolled
/// handlers — the SDK router is the canonical NPS CA contract and covers agents + nodes
/// register/renew/revoke/verify, X.509 (v2) registration, group/session issuance, the
/// enrollment RA flow, plus <c>/.well-known/nps-ca</c>, <c>/v1/ca/cert</c> and <c>/v1/crl</c>,
/// all under the CA's <see cref="NipCaService"/> <c>RoutePrefix</c> (empty → root <c>/v1/...</c>).
/// Mount only when an <see cref="EmbeddedNipCa"/> is registered.
/// </summary>
public static class NipCaEndpoints
{
    public static void Map(WebApplication app, bool mapHealth = true)
    {
        if (mapHealth)
            app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("nip-ca");

        var ca = app.Services.GetRequiredService<EmbeddedNipCa>();
        NipCaRouter.MapNipCa(app, ca.Options, ca.Service);
    }
}
