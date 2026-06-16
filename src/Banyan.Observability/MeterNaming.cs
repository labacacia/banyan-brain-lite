// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Banyan.Observability;

/// <summary>
/// Unified naming conventions for Banyan telemetry, shared by Lite and Pro
/// (Ent mirrors the same shape in its own ServiceDefaults). Meter/ActivitySource
/// names are PascalCase (<c>Banyan.Lite</c>); instrument names are dotted-lower
/// (<c>banyan.lite.queries</c>), matching telemetry.md and version-plan.
/// </summary>
public static class MeterNaming
{
    /// <summary>Meter / ActivitySource name for an edition, e.g. <c>Banyan.Lite</c>.</summary>
    public static string MeterName(string edition) => $"Banyan.{TitleCase(edition)}";

    /// <summary>ActivitySource name for an edition (same convention as the meter).</summary>
    public static string ActivitySourceName(string edition) => MeterName(edition);

    /// <summary>Service resource name, e.g. <c>banyan.lite</c>.</summary>
    public static string ServiceName(string edition) => $"banyan.{Normalize(edition)}";

    /// <summary>Instrument name, e.g. <c>banyan.lite.queries</c>.</summary>
    public static string Instrument(string edition, string suffix)
        => $"banyan.{Normalize(edition)}.{suffix.Trim().TrimStart('.')}";

    private static string Normalize(string edition)
        => (edition ?? string.Empty).Trim().ToLowerInvariant();

    private static string TitleCase(string edition)
    {
        var e = Normalize(edition);
        return e.Length == 0 ? e : char.ToUpper(e[0], CultureInfo.InvariantCulture) + e[1..];
    }
}
