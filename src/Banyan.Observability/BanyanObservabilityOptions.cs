// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Observability;

/// <summary>
/// Configuration for <see cref="ObservabilityDefaults.AddBanyanObservability"/>.
/// Seed it with <see cref="ForEdition"/> and add edition-specific meters /
/// activity sources before wiring.
/// </summary>
public sealed class BanyanObservabilityOptions
{
    public required string Edition { get; init; }

    /// <summary>OTel resource service.name, e.g. <c>banyan.lite</c>.</summary>
    public required string ServiceName { get; set; }

    /// <summary>Meter names registered on the MeterProvider (telemetry.md: one Meter per subsystem).</summary>
    public List<string> Meters { get; } = new();

    /// <summary>ActivitySource names registered on the TracerProvider.</summary>
    public List<string> ActivitySources { get; } = new();

    /// <summary>
    /// Defaults for an edition: service name + the edition's primary meter and
    /// activity source (<c>Banyan.&lt;Edition&gt;</c>). Callers add more as needed.
    /// </summary>
    public static BanyanObservabilityOptions ForEdition(string edition)
    {
        var opts = new BanyanObservabilityOptions
        {
            Edition = edition,
            ServiceName = MeterNaming.ServiceName(edition),
        };
        opts.Meters.Add(MeterNaming.MeterName(edition));
        opts.ActivitySources.Add(MeterNaming.ActivitySourceName(edition));
        return opts;
    }
}
