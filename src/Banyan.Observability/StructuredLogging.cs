// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Banyan.Observability;

/// <summary>
/// Structured-logging helpers (OBS-1). OpenTelemetry already stamps
/// <c>trace_id</c>/<c>span_id</c> onto logs (via <c>IncludeScopes</c>); this adds
/// an application-level correlation id that survives across hops where a trace
/// context may not, and a uniform log scope so every log line carries it.
/// </summary>
public static class StructuredLogging
{
    public const string CorrelationHeader = "X-Correlation-ID";
    public const string CorrelationKey = "banyan.correlation_id";

    /// <summary>
    /// Returns the supplied correlation id when present and non-empty, otherwise the
    /// current trace id, otherwise a fresh id. Pure — usable from any transport adapter.
    /// </summary>
    public static string ResolveCorrelationId(string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
            return supplied.Trim();
        var traceId = Activity.Current?.TraceId.ToString();
        return !string.IsNullOrEmpty(traceId) ? traceId! : Guid.NewGuid().ToString("n");
    }

    /// <summary>Opens a log scope carrying the correlation id so every nested log line includes it.</summary>
    public static IDisposable? BeginCorrelationScope(this ILogger logger, string correlationId)
        => logger.BeginScope(new Dictionary<string, object> { [CorrelationKey] = correlationId });
}
