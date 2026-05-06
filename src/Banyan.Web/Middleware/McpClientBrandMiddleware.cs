// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Banyan.Web.Middleware;

internal sealed class McpClientBrandMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, McpClientBrandRegistry registry)
    {
        if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path == "/mcp")
            await CaptureBrandAsync(ctx, registry);

        await next(ctx);
    }

    private static async Task CaptureBrandAsync(HttpContext ctx, McpClientBrandRegistry registry)
    {
        if (ctx.Request.ContentLength is null or 0)
            return;

        ctx.Request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            if (!root.TryGetProperty("method", out var method) ||
                method.GetString() != "initialize" ||
                !root.TryGetProperty("params", out var p) ||
                !p.TryGetProperty("clientInfo", out var info))
                return;

            var name = info.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = info.TryGetProperty("version", out var v) ? v.GetString() : null;
            registry.Seen(name, version);
        }
        catch (JsonException)
        {
        }
        finally
        {
            ctx.Request.Body.Position = 0;
        }
    }
}
