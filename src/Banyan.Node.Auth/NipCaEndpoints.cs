// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Banyan.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Banyan.Node.Auth;

/// <summary>
/// NPS-3 §8 NIP CA HTTP API. Mirrors the path/shape contract from the reference Go
/// implementation since the .NET <c>NPS.NIP</c> nuget hasn't shipped routing yet.
/// Mount only when an <see cref="EmbeddedNipCa"/> is loaded (DI activation fails otherwise).
/// </summary>
public static class NipCaEndpoints
{
    public sealed record RegisterRequest(
        [property: JsonPropertyName("nid")]          string? Nid,
        [property: JsonPropertyName("pub_key")]      string PubKey,
        [property: JsonPropertyName("capabilities")] string[]? Capabilities,
        [property: JsonPropertyName("scope")]        JsonElement? Scope,
        [property: JsonPropertyName("metadata")]     JsonElement? Metadata);

    public sealed record RevokeRequest(
        [property: JsonPropertyName("reason")] string? Reason);

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("nip-ca");

        app.MapPost("/v1/agents/register", ([FromServices] EmbeddedNipCa ca, RegisterRequest body) =>
            RegisterAsync(ca, body, entityType: "agent")).WithTags("nip-ca");

        app.MapPost("/v1/nodes/register", ([FromServices] EmbeddedNipCa ca, RegisterRequest body) =>
            RegisterAsync(ca, body, entityType: "node")).WithTags("nip-ca");

        app.MapPost("/v1/agents/{nid}/renew", ([FromServices] EmbeddedNipCa ca, string nid) =>
            RenewAsync(ca, nid)).WithTags("nip-ca");

        app.MapPost("/v1/agents/{nid}/revoke", ([FromServices] EmbeddedNipCa ca, string nid, RevokeRequest? body) =>
            RevokeAsync(ca, nid, body?.Reason)).WithTags("nip-ca");

        app.MapGet("/v1/agents/{nid}/verify", ([FromServices] EmbeddedNipCa ca, string nid) =>
            VerifyAsync(ca, nid)).WithTags("nip-ca");

        app.MapGet("/v1/ca/cert", ([FromServices] EmbeddedNipCa ca) => Results.Ok(new
        {
            nid          = ca.CaNid,
            display_name = ca.Options.DisplayName,
            pub_key      = ca.CaPubKey,
            algorithm    = "ed25519",
        })).WithTags("nip-ca");

        app.MapGet("/v1/crl", async ([FromServices] EmbeddedNipCa ca, CancellationToken ct) =>
        {
            var revoked = await ca.ListAsync(revokedOnly: true, ct);
            return Results.Ok(new
            {
                revoked = revoked.Select(r => new
                {
                    nid           = r.Nid,
                    serial        = r.Serial,
                    revoked_at    = r.RevokedAt?.ToString("O"),
                    revoke_reason = r.RevokeReason,
                }).ToArray()
            });
        }).WithTags("nip-ca");

        app.MapGet("/.well-known/nps-ca", ([FromServices] EmbeddedNipCa ca) =>
        {
            var baseUrl = ca.Options.BaseUrl.TrimEnd('/');
            return Results.Ok(new
            {
                nps_ca       = "0.2",
                issuer       = ca.CaNid,
                display_name = ca.Options.DisplayName,
                public_key   = ca.CaPubKey,
                algorithms   = new[] { "ed25519" },
                cert_formats = new[] { "v1-proprietary" },
                endpoints    = new
                {
                    register = $"{baseUrl}/v1/agents/register",
                    verify   = $"{baseUrl}/v1/agents/{{nid}}/verify",
                    ocsp     = $"{baseUrl}/v1/agents/{{nid}}/verify",
                    crl      = $"{baseUrl}/v1/crl",
                },
                capabilities           = new[] { "agent", "node" },
                max_cert_validity_days = Math.Max(ca.Options.AgentCertValidityDays, ca.Options.NodeCertValidityDays),
            });
        }).WithTags("nip-ca");
    }

    // ── handlers ─────────────────────────────────────────────────────────────

    private static async Task<IResult> RegisterAsync(EmbeddedNipCa ca, RegisterRequest body, string entityType)
    {
        if (string.IsNullOrWhiteSpace(body?.PubKey))
            return Results.BadRequest(new { error_code = "NIP-CA-BAD-REQUEST", message = "pub_key required" });

        var identifier = string.IsNullOrEmpty(body.Nid) ? Guid.NewGuid().ToString("N")[..16] : LastSegment(body.Nid);
        var caps  = body.Capabilities ?? Array.Empty<string>();
        var scope = body.Scope?.GetRawText()    ?? "{}";
        var meta  = body.Metadata?.GetRawText() ?? "{}";

        try
        {
            var frame = await ca.Service.RegisterAsync(entityType, identifier, body.PubKey, caps, scope, meta, default);
            return Results.Json(new
            {
                nid         = frame.Nid,
                serial      = frame.Serial,
                issued_at   = frame.IssuedAt,
                expires_at  = frame.ExpiresAt,
                ident_frame = frame,
            }, statusCode: 201);
        }
        catch (Exception ex)
        {
            return Results.Json(new { error_code = "NIP-CA-INTERNAL", message = ex.Message }, statusCode: 500);
        }
    }

    private static async Task<IResult> RenewAsync(EmbeddedNipCa ca, string nid)
    {
        try
        {
            var frame = await ca.RenewAsync(nid);
            return Results.Ok(new
            {
                nid         = frame.Nid,
                serial      = frame.Serial,
                issued_at   = frame.IssuedAt,
                expires_at  = frame.ExpiresAt,
                ident_frame = frame,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error_code = "NIP-CA-NID-NOT-FOUND", message = ex.Message }, statusCode: 404);
        }
    }

    private static async Task<IResult> RevokeAsync(EmbeddedNipCa ca, string nid, string? reason)
    {
        var r = reason ?? "cessation_of_operation";
        try
        {
            var frame = await ca.RevokeAsync(nid, r);
            return Results.Ok(new { nid, revoked_at = frame.RevokedAt, reason = r });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error_code = "NIP-CA-NID-NOT-FOUND", message = ex.Message }, statusCode: 404);
        }
    }

    private static async Task<IResult> VerifyAsync(EmbeddedNipCa ca, string nid)
    {
        var v = await ca.VerifyAsync(nid);
        if (!v.Valid && v.ErrorCode == "NIP-CA-NID-NOT-FOUND")
            return Results.Json(new { error_code = v.ErrorCode, message = v.Message }, statusCode: 404);

        return Results.Ok(new
        {
            valid        = v.Valid,
            nid          = v.Record?.Nid       ?? nid,
            entity_type  = v.Record?.EntityType,
            pub_key      = v.Record?.PubKey,
            capabilities = v.Record?.Capabilities,
            issued_by    = v.Record?.IssuedBy,
            issued_at    = v.Record?.IssuedAt.ToString("O"),
            expires_at   = v.Record?.ExpiresAt.ToString("O"),
            serial       = v.Record?.Serial,
            error_code   = v.Valid ? null : v.ErrorCode,
            message      = v.Valid ? null : v.Message,
        });
    }

    private static string LastSegment(string nid) => nid.Split(':').LastOrDefault() ?? nid;
}
