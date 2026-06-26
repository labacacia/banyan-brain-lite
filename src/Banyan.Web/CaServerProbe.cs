// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using NPS.NIP.Client;

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
            var ownHttp = http is null;
            var client = http ?? new HttpClient();
            client.BaseAddress ??= new Uri(normalized + "/");
            try
            {
                // The discovery doc (/.well-known/nps-ca) is the authoritative source for the CA NID
                // (issuer) + public key. Served by the SDK NipCaRouter that every Banyan CA mounts.
                var ca = new NipCaClient(client);
                var doc = await ca.GetDiscoveryAsync(ct);

                if (!string.IsNullOrWhiteSpace(doc.Issuer) && !string.IsNullOrWhiteSpace(doc.PublicKey))
                    return new(true, normalized, doc.Issuer, doc.PublicKey, doc.DisplayName, "CA server is reachable.");

                return new(false, normalized, null, null, null, "CA server did not return a CA discovery document.");
            }
            finally
            {
                if (ownHttp) client.Dispose();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NipCaClientException)
        {
            return new(false, normalized, null, null, null, ex.Message);
        }
    }
}
