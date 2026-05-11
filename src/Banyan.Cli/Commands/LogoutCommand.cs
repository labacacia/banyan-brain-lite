using System.Text.Json;

namespace Banyan.Cli.Commands;

internal static class LogoutCommand
{
    public static async Task<int> RunAsync(string[] _args)
    {
        var cache = TokenCache.TryLoad();
        if (cache is null)
        {
            Console.WriteLine("Not logged in (no cached token).");
            return 0;
        }

        if (!string.IsNullOrEmpty(cache.RefreshToken))
        {
            try
            {
                using var http = new HttpClient();
                var disco = await http.GetAsync($"{cache.Issuer}/.well-known/openid-configuration");
                if (disco.IsSuccessStatusCode)
                {
                    var doc = JsonDocument.Parse(await disco.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("revocation_endpoint", out var rev))
                    {
                        await http.PostAsync(rev.GetString(),
                            new FormUrlEncodedContent(new Dictionary<string, string>
                            {
                                ["token"]           = cache.RefreshToken,
                                ["token_type_hint"] = "refresh_token",
                                ["client_id"]       = cache.ClientId,
                            }));
                    }
                }
            }
            catch (HttpRequestException)
            {
                Console.WriteLine("(could not reach issuer for revocation; clearing local cache anyway)");
            }
        }

        TokenCache.Clear();
        Console.WriteLine("Logged out.");
        return 0;
    }
}
