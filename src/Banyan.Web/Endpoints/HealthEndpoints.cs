// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Core;
using Banyan.Lite;

namespace Banyan.Web.Endpoints;

public static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/alive", () => Results.Ok(new { status = "alive" }))
            .AllowAnonymous()
            .WithTags("health");

        app.MapGet("/healthz", () => Results.Ok(new { status = "alive" }))
            .AllowAnonymous()
            .WithTags("health");

        app.MapGet("/health", CheckAsync)
            .AllowAnonymous()
            .WithTags("health");

        app.MapGet("/readyz", CheckAsync)
            .AllowAnonymous()
            .WithTags("health");
    }

    private static async Task<IResult> CheckAsync(
        SqliteMemoryStore store,
        IEmbedder embedder,
        WebOptions opts,
        CancellationToken ct)
    {
        var report = await LiteHealthProbe.CheckAsync(
            store, embedder, WebOptions.ExpandHome(opts.MemoryDbPath), ct);
        return report.Status == "ok"
            ? Results.Ok(report)
            : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
