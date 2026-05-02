using Banyan.Auth;
using NPS.NIP.Verification;

namespace Banyan.Web.Middleware;

/// <summary>
/// Server-side counterpart of <see cref="NidAuthHeader"/>. Parses
/// <c>Authorization: NID &lt;base64(IdentFrame JSON)&gt;</c>, runs the upstream
/// <see cref="NipIdentVerifier"/>, and stashes the verified NID + frame into
/// <see cref="HttpContext.Items"/> for downstream endpoints to use as the audit subject.
///
/// Behaviour is gated by <see cref="NidAuthenticationOptions.Mode"/>:
///   AnonymousAllowed  → never blocks; populates Items if a valid frame was supplied
///   WritesRequired    → POST/PUT/DELETE/PATCH must carry a valid NID; reads stay anon
///   AllRequired       → every API request needs a valid NID
///
/// On rejection the response is a NPS-shaped JSON error
/// (<c>{error_code, message}</c>) with a <c>WWW-Authenticate: NID realm="banyan"</c> header
/// — matches the format used by <c>NipCaEndpoints</c> error replies.
/// </summary>
public sealed class NidAuthenticationMiddleware(
    RequestDelegate next,
    NipIdentVerifier verifier,
    NidAuthenticationOptions opts,
    ILogger<NidAuthenticationMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx, EmbeddedNipCa? ca = null)
    {
        var path   = ctx.Request.Path.Value ?? "";
        var method = ctx.Request.Method;

        // Static assets and SPA root: never gated.
        if (!IsApiRoute(path)) { await next(ctx); return; }

        // Liveness / manifest / discovery always pass through.
        if (IsPublicPath(path, opts.PublicPaths)) { await next(ctx); return; }

        var auth = ctx.Request.Headers.Authorization.ToString();
        IdentFrameVerificationOutcome outcome = IdentFrameVerificationOutcome.NoHeader;
        NPS.NIP.Frames.IdentFrame? frame = null;
        string? errorCode = null, errorMsg = null;

        if (!string.IsNullOrWhiteSpace(auth))
        {
            frame = NidAuthHeader.TryParse(auth);
            if (frame is null)
            {
                outcome   = IdentFrameVerificationOutcome.Malformed;
                errorCode = "NIP-AUTH-MALFORMED";
                errorMsg  = "Could not decode Authorization: NID base64(IdentFrame)";
            }
            else
            {
                // Lite: identity-only verification. Per-node scope (`TargetNodePath`)
                // would force every IdentFrame to enumerate every path it can hit, which is
                // a Pro/Ent multi-tenant concern. Authorization layers can re-check the frame
                // (stashed in Items) with whatever scope they need.
                var verifyCtx = new NipVerifyContext { AsOf = DateTime.UtcNow };
                try
                {
                    var result = await verifier.VerifyAsync(frame, verifyCtx, ctx.RequestAborted);
                    if (!result.IsValid)
                    {
                        outcome   = IdentFrameVerificationOutcome.Invalid;
                        errorCode = result.ErrorCode ?? "NIP-AUTH-INVALID";
                        errorMsg  = result.Message   ?? $"verification failed at step {result.FailedStep}";
                    }
                    else if (ca is not null)
                    {
                        // The static `LocalRevokedSerials` set on NipVerifierOptions doesn't see
                        // runtime revocations from the embedded CA, so consult the CA's authoritative
                        // status before granting the request.
                        var caStatus = await ca.VerifyAsync(frame.Nid, ctx.RequestAborted);
                        if (!caStatus.Valid)
                        {
                            outcome   = IdentFrameVerificationOutcome.Invalid;
                            errorCode = caStatus.ErrorCode ?? "NIP-AUTH-REVOKED";
                            errorMsg  = caStatus.Message   ?? "certificate is revoked or expired";
                        }
                        else
                        {
                            outcome = IdentFrameVerificationOutcome.Valid;
                            ctx.Items[NidAuthenticationOptions.ContextKeyNid]   = frame.Nid;
                            ctx.Items[NidAuthenticationOptions.ContextKeyFrame] = frame;
                        }
                    }
                    else
                    {
                        outcome = IdentFrameVerificationOutcome.Valid;
                        ctx.Items[NidAuthenticationOptions.ContextKeyNid]   = frame.Nid;
                        ctx.Items[NidAuthenticationOptions.ContextKeyFrame] = frame;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "nid-auth: NipIdentVerifier threw on frame {Nid}", frame.Nid);
                    outcome   = IdentFrameVerificationOutcome.Invalid;
                    errorCode = "NIP-AUTH-VERIFIER-ERROR";
                    errorMsg  = ex.Message;
                }
            }
        }

        bool needAuth = NeedsAuth(opts.Mode, method);
        if (needAuth && outcome != IdentFrameVerificationOutcome.Valid)
        {
            ctx.Response.StatusCode = outcome == IdentFrameVerificationOutcome.Malformed ? 400 : 401;
            ctx.Response.Headers["WWW-Authenticate"] = $"{NidAuthenticationOptions.AuthScheme} realm=\"banyan\"";
            await ctx.Response.WriteAsJsonAsync(new
            {
                error_code = errorCode ?? "NIP-AUTH-REQUIRED",
                message    = errorMsg  ?? "Missing Authorization: NID header",
            });
            log.LogInformation("nid-auth: rejected {Method} {Path}: {Code}", method, path, errorCode ?? "missing");
            return;
        }

        await next(ctx);
    }

    private static bool IsApiRoute(string path) =>
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/",  StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v2/",  StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicPath(string path, IReadOnlyList<string> publicPaths) =>
        publicPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase));

    private static bool NeedsAuth(NidAuthMode mode, string httpMethod) => mode switch
    {
        NidAuthMode.AnonymousAllowed => false,
        NidAuthMode.WritesRequired   => httpMethod is "POST" or "PUT" or "DELETE" or "PATCH",
        NidAuthMode.AllRequired      => true,
        _                            => false,
    };

    private enum IdentFrameVerificationOutcome { NoHeader, Malformed, Invalid, Valid }
}

/// <summary>Convenience extensions to mount <see cref="NidAuthenticationMiddleware"/>.</summary>
public static class NidAuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseNidAuthentication(this IApplicationBuilder app)
        => app.UseMiddleware<NidAuthenticationMiddleware>();
}
