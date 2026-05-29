// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Auth;

/// <summary>
/// Top-level auth mode shared across all Banyan editions.
/// Each edition supports a subset of modes; configure via <c>Banyan:AuthMode</c>.
/// </summary>
public enum BanyanAuthMode
{
    /// <summary>
    /// No authentication — all requests accepted without credentials.
    /// Intended for fully local / air-gapped Lite deployments.
    /// </summary>
    Offline,

    /// <summary>
    /// Local authentication — built-in admin account + embedded identity server.
    /// Pro default; Ent local mode.
    /// </summary>
    Local,

    /// <summary>
    /// Hub IAM — JWT tokens and NID certificates issued by an external Banyan Hub
    /// or OIDC-compatible identity provider.
    /// All editions support Hub mode.
    /// </summary>
    Hub,
}
