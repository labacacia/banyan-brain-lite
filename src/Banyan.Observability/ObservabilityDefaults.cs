// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Banyan.Observability;

/// <summary>
/// Shared OpenTelemetry wiring for Banyan editions (OBS-1). Mirrors the Ent
/// Aspire ServiceDefaults pattern so Lite/Pro/Ent behave the same:
/// <list type="bullet">
///   <item>metrics — ASP.NET Core + HttpClient + runtime instrumentation + the edition meters</item>
///   <item>traces  — ASP.NET Core + HttpClient instrumentation + the edition activity sources, ParentBased sampling</item>
///   <item>logs    — formatted message + scopes</item>
///   <item>OTLP export only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (single-node stays quiet)</item>
/// </list>
/// telemetry.md principles apply: one Meter per subsystem; tenant_id is never a metric tag.
/// </summary>
public static class ObservabilityDefaults
{
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string TraceSamplerRatioKey = "OTEL_TRACE_SAMPLER_RATIO";

    public static IHostApplicationBuilder AddBanyanObservability(
        this IHostApplicationBuilder builder,
        string edition,
        Action<BanyanObservabilityOptions>? configure = null)
    {
        var options = BanyanObservabilityOptions.ForEdition(edition);
        configure?.Invoke(options);

        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: options.ServiceName))
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                foreach (var meter in options.Meters)
                    metrics.AddMeter(meter);
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .SetSampler(BuildSampler(builder.Configuration));
                foreach (var source in options.ActivitySources)
                    tracing.AddSource(source);
            });

        if (OtlpEnabled(builder.Configuration))
        {
            builder.Services.Configure<OpenTelemetryLoggerOptions>(l => l.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryMeterProvider(m => m.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddOtlpExporter());
        }

        return builder;
    }

    /// <summary>True when an OTLP endpoint is configured; otherwise nothing is exported.</summary>
    public static bool OtlpEnabled(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]);

    private static Sampler BuildSampler(IConfiguration configuration)
    {
        var ratio = configuration[TraceSamplerRatioKey];
        return new ParentBasedSampler(
            string.IsNullOrWhiteSpace(ratio)
                ? new AlwaysOnSampler()
                : new TraceIdRatioBasedSampler(double.Parse(ratio, CultureInfo.InvariantCulture)));
    }
}
