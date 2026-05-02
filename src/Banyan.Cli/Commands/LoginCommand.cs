using System.Net.Http.Json;
using System.Text.Json;

namespace Banyan.Cli.Commands;

/// <summary>
/// Drives an OIDC login flow against <see cref="Banyan.Identity.BanyanIdentityOptions.Issuer"/>.
/// Default: Device Code flow (CLI-friendly). With <c>--browser</c>: Authorization Code + PKCE
/// using a loopback redirect (RFC 8252).
/// </summary>
internal static class LoginCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opts     = CommandContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        var issuer   = (CommandContext.GetOption(args, "--issuer")  ?? opts.Issuer).TrimEnd('/');
        var clientId = CommandContext.GetOption(args, "--client-id") ?? opts.CliClientId;
        var browser  = CommandContext.HasFlag(args, "--browser");

        if (browser)
        {
            Console.Error.WriteLine("login --browser: Authorization Code + PKCE flow not yet implemented (P3).");
            return 9;
        }

        return await DeviceCodeAsync(issuer, clientId);
    }

    private static async Task<int> DeviceCodeAsync(string issuer, string clientId)
    {
        using var http = new HttpClient();

        // 1) Discover device authorization endpoint via /.well-known/openid-configuration
        var discoveryUrl = $"{issuer}/.well-known/openid-configuration";
        var discoveryDoc = await SafeGetJsonAsync(http, discoveryUrl);
        if (discoveryDoc is null) return 4;

        var deviceUri = discoveryDoc.RootElement.TryGetProperty("device_authorization_endpoint", out var d)
            ? d.GetString() : null;
        var tokenUri = discoveryDoc.RootElement.GetProperty("token_endpoint").GetString();
        if (string.IsNullOrEmpty(deviceUri) || string.IsNullOrEmpty(tokenUri))
        {
            Console.Error.WriteLine("login: issuer does not advertise a device_authorization_endpoint.");
            return 5;
        }

        // 2) Request a device code
        var deviceResp = await http.PostAsync(deviceUri,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"]     = "openid profile banyan.full",
            }));
        if (!deviceResp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"login: device-authorization failed: {deviceResp.StatusCode}");
            return 6;
        }
        var deviceJson = await deviceResp.Content.ReadFromJsonAsync<JsonElement>();
        var deviceCode = deviceJson.GetProperty("device_code").GetString()!;
        var userCode   = deviceJson.GetProperty("user_code").GetString()!;
        var verifyUrl  = deviceJson.GetProperty("verification_uri").GetString();
        var interval   = deviceJson.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
        var expiresIn  = deviceJson.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 600;

        Console.WriteLine();
        Console.WriteLine($"To log in, open: {verifyUrl}");
        Console.WriteLine($"Enter user code:  {userCode}");
        Console.WriteLine();

        // 3) Poll the token endpoint
        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval));
            var tokenResp = await http.PostAsync(tokenUri,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode,
                    ["client_id"]   = clientId,
                }));
            var tokenJson = await tokenResp.Content.ReadFromJsonAsync<JsonElement>();

            if (tokenResp.IsSuccessStatusCode)
            {
                var cache = new TokenCache
                {
                    AccessToken  = tokenJson.GetProperty("access_token").GetString()!,
                    RefreshToken = tokenJson.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                    Issuer       = issuer,
                    ClientId     = clientId,
                    ExpiresAt    = DateTimeOffset.UtcNow.AddSeconds(
                        tokenJson.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 1800),
                };
                cache.Save();
                Console.WriteLine("Login successful.");
                return 0;
            }

            var err = tokenJson.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            if (err == "authorization_pending" || err == "slow_down")
            {
                if (err == "slow_down") interval += 5;
                continue;
            }
            Console.Error.WriteLine($"login failed: {err}");
            return 7;
        }

        Console.Error.WriteLine("login: device code expired before authorization.");
        return 8;
    }

    private static async Task<JsonDocument?> SafeGetJsonAsync(HttpClient http, string url)
    {
        try
        {
            var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"login: GET {url} → {resp.StatusCode}");
                return null;
            }
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"login: cannot reach {url} ({ex.Message}). Is `banyan serve` running?");
            return null;
        }
    }
}
