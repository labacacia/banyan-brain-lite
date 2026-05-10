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
            RemoteNipCaClient.CaCertResponse? cert = null;
            try { cert = await client.CaCertAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or RemoteNipCaException) { }

            if (cert is not null &&
                !string.IsNullOrWhiteSpace(cert.Nid) &&
                !string.IsNullOrWhiteSpace(cert.PubKey))
            {
                return new(true, normalized, cert.Nid, cert.PubKey, cert.DisplayName, "CA server is reachable.");
            }

            var wellKnown = await client.WellKnownAsync(ct);
            if (wellKnown is not null &&
                !string.IsNullOrWhiteSpace(wellKnown.NpsCa) &&
                !string.IsNullOrWhiteSpace(wellKnown.PublicKey))
            {
                return new(true, normalized, wellKnown.NpsCa, wellKnown.PublicKey, wellKnown.DisplayName, "CA server is reachable.");
            }

            return new(false, normalized, null, null, null, "CA server did not return a CA certificate.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RemoteNipCaException)
        {
            return new(false, normalized, null, null, null, ex.Message);
        }
    }
}
