// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Web.Endpoints;

namespace Banyan.Web.Middleware;

/// <summary>
/// If the incoming request has the <c>banyan_session</c> cookie but no <c>Authorization</c>
/// header, copies the cookie value onto <c>Authorization: Bearer &lt;token&gt;</c> so the
/// standard JWT bearer pipeline can validate it. Lets the browser persist sessions via an
/// HttpOnly cookie without forking the auth code into "two ways to read a token".
/// </summary>
public sealed class SessionCookieMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Headers.ContainsKey("Authorization") &&
            ctx.Request.Cookies.TryGetValue(BrowserAuthEndpoints.SessionCookieName, out var token) &&
            !string.IsNullOrEmpty(token))
        {
            ctx.Request.Headers.Authorization = $"Bearer {token}";
        }
        await next(ctx);
    }
}

public static class SessionCookieMiddlewareExtensions
{
    public static IApplicationBuilder UseSessionCookie(this IApplicationBuilder app)
        => app.UseMiddleware<SessionCookieMiddleware>();
}
