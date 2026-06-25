// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Auth;

namespace Banyan.Web;

public sealed record CaServerProbeResult(
    bool Ok,
    string? Address,
    string? CaNid,
    string? PublicKey,
    string? DisplayName,
    string? Message);

public static class CaServerProbe
{
    public static async Task<CaServerProbeResult> TestExternalAsync(
        string? address,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new(false, null, null, null, null, "CA server address is required.");

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return new(false, address.Trim(), null, null, null, "CA server address must be an absolute http(s) URL.");

        var normalized = uri.ToString().TrimEnd('/');
        try
        {
            using var client = new RemoteNipCaClient(normalized, http);

            // The discovery doc is the authoritative source for the CA NID (issuer) + public key;
            // the canonical /v1/ca/cert only returns the key (no NID).
            RemoteNipCaClient.WellKnownResponse? wellKnown = null;
            try { wellKnown = await client.WellKnownAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or RemoteNipCaException) { }

            if (wellKnown is not null &&
                !string.IsNullOrWhiteSpace(wellKnown.Issuer) &&
                !string.IsNullOrWhiteSpace(wellKnown.PublicKey))
            {
                return new(true, normalized, wellKnown.Issuer, wellKnown.PublicKey, wellKnown.DisplayName, "CA server is reachable.");
            }

            // Fallback: confirm a CA key is served even without a discovery doc (NID then unknown).
            var cert = await client.CaCertAsync(ct);
            if (cert is not null && !string.IsNullOrWhiteSpace(cert.PublicKey))
                return new(true, normalized, null, cert.PublicKey, null, "CA server reachable (no discovery doc; CA NID unknown).");

            return new(false, normalized, null, null, null, "CA server did not return a CA certificate.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RemoteNipCaException)
        {
            return new(false, normalized, null, null, null, ex.Message);
        }
    }
}
