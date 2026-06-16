// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.Metrics;
using Banyan.Lite;
using Xunit;

namespace Banyan.Lite.Tests;

public class BanyanLiteMetricsTests
{
    [Fact]
    public void RecordQuery_EmitsCounterAndHistogram()
    {
        long queryCount = 0;
        double? duration = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == BanyanLiteMetrics.MeterName) l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, m, _, _) =>
        {
            if (inst.Name == "banyan.lite.queries") queryCount += m;
        });
        listener.SetMeasurementEventCallback<double>((inst, m, _, _) =>
        {
            if (inst.Name == "banyan.lite.query_duration_ms") duration = m;
        });
        listener.Start();

        using var metrics = new BanyanLiteMetrics();
        metrics.RecordQuery(12.5);

        Assert.Equal(1, queryCount);
        Assert.Equal(12.5, duration);
    }

    [Fact]
    public void ObservableGauges_ReportInjectedValues()
    {
        using var metrics = new BanyanLiteMetrics(memoriesTotal: () => 42, poolMembers: () => 7);

        var observed = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == BanyanLiteMetrics.MeterName) l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, m, _, _) => observed[inst.Name] = m);
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(42, observed["banyan.lite.memories_total"]);
        Assert.Equal(7, observed["banyan.lite.pool_members"]);
    }
}
