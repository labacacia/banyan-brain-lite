// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Auth;

/// <summary>
/// How aggressively a server enforces <see cref="NPS.NIP.Frames.IdentFrame"/> authentication.
/// Lite ships with <see cref="AnonymousAllowed"/> by default; Pro / production should run
/// <see cref="WritesRequired"/> or <see cref="AllRequired"/>.
/// </summary>
public enum NidAuthMode
{
    /// <summary>Lite default — NID parsed if a client supplies one (so audit columns get a real value), but never required.</summary>
    AnonymousAllowed,

    /// <summary>Reads anonymous; writes (POST / PUT / DELETE / PATCH) must carry a valid NID.</summary>
    WritesRequired,

    /// <summary>All API requests must carry a valid NID. Pro tier default.</summary>
    AllRequired,
}

/// <summary>Configuration for the NID authentication middleware. Paths in <see cref="PublicPaths"/> always bypass auth.</summary>
public sealed class NidAuthenticationOptions
{
    public NidAuthMode Mode { get; set; } = NidAuthMode.AnonymousAllowed;

    /// <summary>Always allow these paths through, regardless of <see cref="Mode"/> (liveness, manifest, CA discovery).</summary>
    public string[] PublicPaths { get; set; } =
    [
        "/api/health", "/health", "/alive",
        "/.nwm", "/.schema",
        "/v1/ca/cert", "/v1/crl", "/.well-known/nps-ca",
    ];

    /// <summary>Header name. Spec is fixed at <c>Authorization: NID …</c>.</summary>
    public const string AuthScheme = "NID";

    /// <summary>HttpContext.Items keys the middleware writes after a successful verification.</summary>
    public const string ContextKeyNid    = "banyan.agent_nid";
    public const string ContextKeyFrame  = "banyan.ident_frame";
}
