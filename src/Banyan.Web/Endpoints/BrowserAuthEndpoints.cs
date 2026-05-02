using System.Security.Claims;
using OLS.Root.Authentication.Managers;
using OLS.Root.Core.Models;

namespace Banyan.Web.Endpoints;

/// <summary>
/// Browser-side login endpoints. Wraps OLS's <see cref="SignInManager{TUser}"/> so the demo
/// web UI can do "form POST → JWT cookie" without speaking the raw OIDC dance. The CLI
/// continues to use the full Device Code flow against <c>/connect/*</c>.
///
/// The cookie is HttpOnly + SameSite=Strict and contains the access token verbatim;
/// <see cref="Middleware.SessionCookieMiddleware"/> lifts it onto <c>Authorization: Bearer</c>
/// so the standard JWT validator handles auth.
/// </summary>
public static class BrowserAuthEndpoints
{
    public const string SessionCookieName = "banyan_session";

    public sealed record LoginBody(string Username, string Password);
    public sealed record LoginResponse(string Username, DateTimeOffset ExpiresAt);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/auth").WithTags("auth");

        g.MapPost("/login", async (HttpContext ctx, LoginBody body, ISignInManager<IdentityUser> signIn) =>
        {
            if (string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password))
                return Results.BadRequest(new { error_code = "AUTH-MISSING-CREDS", message = "username and password are required" });

            var result = await signIn.PasswordSignInAsync(body.Username, body.Password);
            if (!result.Succeeded || string.IsNullOrEmpty(result.AccessToken))
            {
                var msg = result.Errors is { Count: > 0 } errs ? string.Join("; ", errs) : "invalid username or password";
                return Results.Json(new { error_code = "AUTH-INVALID-CREDS", message = msg }, statusCode: 401);
            }

            var expires = result.AccessTokenExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);

            ctx.Response.Cookies.Append(SessionCookieName, result.AccessToken!, new CookieOptions
            {
                HttpOnly = true,
                Secure   = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires  = expires,
                Path     = "/",
            });

            return Results.Ok(new LoginResponse(
                Username:  body.Username,
                ExpiresAt: expires));
        }).AllowAnonymous();

        g.MapPost("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(SessionCookieName, new CookieOptions { Path = "/" });
            return Results.Ok(new { ok = true });
        }).AllowAnonymous();

        // /api/auth/me — succeeds only if the current request carries a valid JWT (cookie or Bearer).
        // Replaces the older /api/identity/me which only decoded a local file cache.
        g.MapGet("/me", (HttpContext ctx) =>
        {
            var user = ctx.User;
            if (user.Identity is null || !user.Identity.IsAuthenticated)
                return Results.Json(new { loggedIn = false }, statusCode: 200);

            return Results.Ok(new
            {
                loggedIn = true,
                subject  = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"),
                username = user.Identity.Name ?? user.FindFirstValue("preferred_username"),
                roles    = user.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "role")
                                       .Select(c => c.Value).ToArray(),
                expiresAt = long.TryParse(user.FindFirstValue("exp"), out var exp)
                    ? DateTimeOffset.FromUnixTimeSeconds(exp) : (DateTimeOffset?)null,
            });
        }).AllowAnonymous();
    }
}
