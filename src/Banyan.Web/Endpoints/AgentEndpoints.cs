using Banyan.Auth;
using NPS.NIP.Crypto;
using NSec.Cryptography;

namespace Banyan.Web.Endpoints;

public static class AgentEndpoints
{
    public sealed record IssueBody(string Id, string[]? Capabilities = null);
    public sealed record IssueResponse(
        string Nid, string Serial, string IssuedBy, string ExpiresAt, string[] Capabilities,
        string PrivateKeyBase64,
        string Note);

    public sealed record AgentRow(
        string Nid, string Serial, string EntityType,
        string IssuedAt, string ExpiresAt,
        string Status, string? RevokeReason);

    public sealed record RevokeBody(string? Reason);

    public sealed record VerifyResponse(bool Valid, string? ErrorCode, string? Message, string? ExpiresAt);

    public static void Map(WebApplication app, bool requireAdmin)
    {
        var g = app.MapGroup("/api/agents").WithTags("agents");
        // Issuing / listing / revoking agent NIDs is operator-only. When OLS identity isn't wired
        // (Lite zero-config posture) we fall through unauthenticated — same posture as before.
        if (requireAdmin) g.RequireAuthorization("admin");

        g.MapPost("/", async (IssueBody body, EmbeddedNipCa ca) =>
        {
            if (string.IsNullOrWhiteSpace(body.Id))
                return Results.BadRequest(new { error = "id required" });

            // Generate the agent's keypair on the server, register, and return both halves.
            // The private key is shown ONCE; the operator copies it out of the UI.
            var algo = SignatureAlgorithm.Ed25519;
            using var key = Key.Create(algo, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            var pubKey  = NipSigner.EncodePublicKey(key.PublicKey);
            var privRaw = key.Export(KeyBlobFormat.RawPrivateKey);

            var caps  = body.Capabilities ?? Array.Empty<string>();
            var frame = await ca.RegisterAgentAsync(body.Id, pubKey, caps);

            return Results.Ok(new IssueResponse(
                Nid:              frame.Nid,
                Serial:           frame.Serial,
                IssuedBy:         frame.IssuedBy,
                ExpiresAt:        frame.ExpiresAt,
                Capabilities:     caps,
                PrivateKeyBase64: Convert.ToBase64String(privRaw),
                Note:             "Save the private key now — it will not be shown again."));
        });

        g.MapGet("/", async (EmbeddedNipCa ca, bool? revokedOnly) =>
        {
            var rows = await ca.ListAsync(revokedOnly ?? false);
            var now  = DateTime.UtcNow;
            var dto  = rows.Select(r => new AgentRow(
                r.Nid, r.Serial, r.EntityType,
                r.IssuedAt.ToString("O"), r.ExpiresAt.ToString("O"),
                r.RevokedAt.HasValue ? "revoked" : (r.ExpiresAt < now ? "expired" : "active"),
                r.RevokeReason)).ToArray();
            return Results.Ok(dto);
        });

        g.MapPost("/{nid}/revoke", async (string nid, RevokeBody body, EmbeddedNipCa ca) =>
        {
            var reason = string.IsNullOrEmpty(body?.Reason) ? "operator-initiated" : body.Reason!;
            var frame  = await ca.RevokeAsync(nid, reason);
            return Results.Ok(new { nid, reason, revokedAt = frame.RevokedAt });
        });

        g.MapGet("/{nid}/verify", async (string nid, EmbeddedNipCa ca) =>
        {
            var r = await ca.VerifyAsync(nid);
            return Results.Ok(new VerifyResponse(
                r.Valid, r.ErrorCode, r.Message, r.Record?.ExpiresAt.ToString("O")));
        });
    }
}
