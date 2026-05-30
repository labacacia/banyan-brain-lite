using Banyan.Core;
using Banyan.Lite;

namespace Banyan.Node;

public static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/alive", () => Results.Ok(new { status = "alive" }))
            .AllowAnonymous()
            .WithTags("health");

        app.MapGet("/health", CheckAsync)
            .AllowAnonymous()
            .WithTags("health");
    }

    private static async Task<IResult> CheckAsync(
        SqliteMemoryStore store,
        IEmbedder embedder,
        CancellationToken ct)
    {
        var report = await LiteHealthProbe.CheckAsync(store, embedder, ct);
        return report.Status == "ok"
            ? Results.Ok(report)
            : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
