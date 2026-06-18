// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.IdentityModel.Tokens.Jwt;

namespace Banyan.Cli.Commands;

internal static class WhoamiCommand
{
    public static int Run(string[] _args)
    {
        var cache = TokenCache.TryLoad();
        if (cache is null)
        {
            Console.Error.WriteLine("Not logged in. Run `banyan login`.");
            return 1;
        }

        JwtSecurityToken jwt;
        try { jwt = new JwtSecurityTokenHandler().ReadJwtToken(cache.AccessToken); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cached token is malformed: {ex.Message}");
            return 2;
        }

        Console.WriteLine($"issuer:   {cache.Issuer}");
        Console.WriteLine($"client:   {cache.ClientId}");
        Console.WriteLine($"subject:  {jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? "(unknown)"}");
        Console.WriteLine($"name:     {jwt.Claims.FirstOrDefault(c => c.Type == "name" || c.Type == "preferred_username")?.Value ?? "(unknown)"}");
        Console.WriteLine($"scopes:   {jwt.Claims.FirstOrDefault(c => c.Type == "scope")?.Value ?? "(none)"}");
        Console.WriteLine($"expires:  {cache.ExpiresAt:O}  ({(cache.ExpiresAt < DateTimeOffset.UtcNow ? "EXPIRED" : "valid")})");
        return 0;
    }
}
