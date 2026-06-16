// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;

namespace Banyan.Lite;

/// <summary>
/// OpenTelemetry instruments for Lite (OBS-3). Meter name <c>Banyan.Lite</c>
/// (registered + exported by Banyan.Observability). Pure BCL metrics — no
/// dependency on the observability package, so it stays testable in isolation.
/// telemetry.md principle: counters/histograms for query traffic, polled
/// ObservableGauges for cardinality (no per-event gauge mutation).
/// </summary>
public sealed class BanyanLiteMetrics : IDisposable
{
    public const string MeterName = "Banyan.Lite";

    private readonly Meter _meter;
    public Counter<long> Queries { get; }
    public Histogram<double> QueryDurationMs { get; }

    public BanyanLiteMetrics(Func<long>? memoriesTotal = null, Func<long>? poolMembers = null)
    {
        _meter = new Meter(MeterName, "1.0.0");
        Queries = _meter.CreateCounter<long>("banyan.lite.queries", "{query}",
            "Memory search queries served.");
        QueryDurationMs = _meter.CreateHistogram<double>("banyan.lite.query_duration_ms", "ms",
            "Wall-clock duration of a memory search query.");

        _meter.CreateObservableGauge("banyan.lite.memories_total",
            () => memoriesTotal?.Invoke() ?? 0L, "{memory}", "Total memories in the store.");
        _meter.CreateObservableGauge("banyan.lite.pool_members",
            () => poolMembers?.Invoke() ?? 0L, "{member}", "Total pool membership entries.");
    }

    /// <summary>Records one served query and its duration.</summary>
    public void RecordQuery(double durationMs)
    {
        Queries.Add(1);
        QueryDurationMs.Record(durationMs);
    }

    public void Dispose() => _meter.Dispose();
}
