// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;
using Banyan.Core.Isolation;
using Microsoft.AspNetCore.Http;
using NPS.NIP.Frames;

namespace Banyan.Node.Auth;

/// <summary>
/// Lite implementation of the unified isolation seam (ISO-4). Builds an
/// <see cref="IsolationContext"/> from the NID identity that
/// <see cref="NidAuthenticationMiddleware"/> has already verified and stashed in
/// <see cref="HttpContext.Items"/>, then enforces capabilities through the shared
/// <see cref="IIsolationEnforcer"/>.
///
/// Capability enforcement applies only to <b>authenticated</b> principals.
/// Anonymous access remains governed by <see cref="NidAuthMode"/> (the middleware),
/// so enabling this does not change behaviour under
/// <c>AnonymousAllowed</c>/<c>WritesRequired</c> for anonymous callers — it closes
/// the gap where an authenticated agent's issued capabilities were never checked.
/// </summary>
public static class LiteIsolation
{
    /// <summary>Resolves the isolation context for the current request from the verified NID frame.</summary>
    public static IsolationContext Resolve(HttpContext http, string @namespace = IsolationDefaults.DefaultNamespace)
    {
        var nid = http.Items[NidAuthenticationOptions.ContextKeyNid] as string;
        if (string.IsNullOrEmpty(nid))
            return IsolationContext.Local(@namespace, PrincipalRef.Anonymous);

        var frame = http.Items[NidAuthenticationOptions.ContextKeyFrame] as IdentFrame;
        var caps = new HashSet<string>(
            frame?.Capabilities ?? Array.Empty<string>(), StringComparer.Ordinal);
        var principal = new PrincipalRef(nid, frame?.FrameType.ToString(), caps);
        return IsolationContext.Local(@namespace, principal);
    }

    /// <summary>
    /// Returns <c>null</c> when the request may proceed, or a 403 <see cref="IResult"/> when an
    /// authenticated principal lacks <paramref name="capability"/>. Anonymous principals always
    /// pass here (they are gated by the auth mode, not capabilities).
    /// </summary>
    public static IResult? Authorize(HttpContext http, IIsolationEnforcer enforcer, string capability)
    {
        var ctx = Resolve(http);
        if (ReferenceEquals(ctx.Principal, PrincipalRef.Anonymous))
            return null;

        try
        {
            enforcer.RequireCapability(ctx, capability);
            return null;
        }
        catch (IsolationDeniedException ex)
        {
            return Results.Json(
                new { error_code = "NIP-AUTHZ-CAPABILITY", message = ex.Reason },
                statusCode: StatusCodes.Status403Forbidden);
        }
    }
}
