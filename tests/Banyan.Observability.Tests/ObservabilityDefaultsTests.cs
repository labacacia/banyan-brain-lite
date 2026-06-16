// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace Banyan.Observability.Tests;

public class ObservabilityDefaultsTests
{
    // ── naming conventions (OBS-1) ──────────────────────────────────────────────
    [Theory]
    [InlineData("lite", "Banyan.Lite", "banyan.lite")]
    [InlineData("pro", "Banyan.Pro", "banyan.pro")]
    [InlineData("Ent", "Banyan.Ent", "banyan.ent")]
    public void MeterNaming_FollowsConvention(string edition, string meter, string service)
    {
        Assert.Equal(meter, MeterNaming.MeterName(edition));
        Assert.Equal(meter, MeterNaming.ActivitySourceName(edition));
        Assert.Equal(service, MeterNaming.ServiceName(edition));
        Assert.Equal($"{service}.queries", MeterNaming.Instrument(edition, "queries"));
    }

    [Fact]
    public void ForEdition_SeedsServiceMeterAndSource()
    {
        var o = BanyanObservabilityOptions.ForEdition("lite");
        Assert.Equal("banyan.lite", o.ServiceName);
        Assert.Contains("Banyan.Lite", o.Meters);
        Assert.Contains("Banyan.Lite", o.ActivitySources);
    }

    // ── OTLP gate (export only when endpoint configured) ────────────────────────
    [Fact]
    public void OtlpEnabled_FalseWithoutEndpoint()
    {
        var cfg = new ConfigurationBuilder().Build();
        Assert.False(ObservabilityDefaults.OtlpEnabled(cfg));
    }

    [Fact]
    public void OtlpEnabled_TrueWhenEndpointSet()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ObservabilityDefaults.OtlpEndpointKey] = "http://localhost:4317",
            }).Build();
        Assert.True(ObservabilityDefaults.OtlpEnabled(cfg));
    }

    // ── host smoke: wiring registers providers and does not throw ───────────────
    [Fact]
    public void AddBanyanObservability_RegistersProviders()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddBanyanObservability("lite", o => o.Meters.Add("Banyan.Lite.Extra"));
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetService<MeterProvider>());
        Assert.NotNull(host.Services.GetService<TracerProvider>());
    }

    // ── correlation id resolution ───────────────────────────────────────────────
    [Fact]
    public void ResolveCorrelationId_UsesSuppliedWhenPresent()
        => Assert.Equal("abc-123", StructuredLogging.ResolveCorrelationId("  abc-123 "));

    [Fact]
    public void ResolveCorrelationId_GeneratesWhenAbsent()
    {
        var id = StructuredLogging.ResolveCorrelationId(null);
        Assert.False(string.IsNullOrWhiteSpace(id));
    }
}
