using Banyan.Identity;
using OLS.Root.Core.Models;
using OLS.Root.Core.Security;

namespace Banyan.Web.Endpoints;

public static class SetupEndpoints
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public sealed record SetupStatusResponse(bool SetupRequired, bool IdentityEnabled);
    public sealed record AdminSetupBody(string Username, string Password);
    public sealed record AdminSetupResponse(string Username);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/setup").WithTags("setup");

        g.MapGet("/status", async (SqliteIdentityStore store, CancellationToken ct) =>
        {
            var setupRequired = !await AdminBootstrapper.HasAdminAsync(store, ct);
            return Results.Ok(new SetupStatusResponse(setupRequired, IdentityEnabled: true));
        }).AllowAnonymous();

        g.MapPost("/admin", async (
            AdminSetupBody body,
            SqliteIdentityStore store,
            IPasswordHasher<IdentityUser> hasher,
            CancellationToken ct) =>
        {
            var username = string.IsNullOrWhiteSpace(body.Username) ? "admin" : body.Username.Trim();
            if (username.Length < 3)
                return Results.BadRequest(new { error_code = "SETUP-USERNAME-SHORT", message = "username must be at least 3 characters" });
            if (string.IsNullOrEmpty(body.Password) || body.Password.Length < 10)
                return Results.BadRequest(new { error_code = "SETUP-PASSWORD-SHORT", message = "password must be at least 10 characters" });

            await SetupLock.WaitAsync(ct);
            try
            {
                if (await AdminBootstrapper.HasAdminAsync(store, ct))
                {
                    return Results.Json(new
                    {
                        error_code = "SETUP-ALREADY-COMPLETE",
                        message = "admin setup has already been completed",
                    }, statusCode: StatusCodes.Status409Conflict);
                }

                var result = await AdminBootstrapper.CreateInitialAdminAsync(store, hasher, username, body.Password, ct);
                if (!result.Succeeded)
                {
                    var message = string.Join("; ", result.Errors.Select(e => e.Description));
                    return Results.Json(new
                    {
                        error_code = "SETUP-CREATE-FAILED",
                        message,
                    }, statusCode: StatusCodes.Status400BadRequest);
                }

                return Results.Ok(new AdminSetupResponse(username));
            }
            finally
            {
                SetupLock.Release();
            }
        }).AllowAnonymous();
    }
}
