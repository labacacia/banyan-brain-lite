using System.Text;
using System.Text.Json;
using NPS.NIP.Frames;

namespace Banyan.Auth;

/// <summary>
/// Client-side helpers to build / parse the <c>Authorization: NID &lt;base64(IdentFrame JSON)&gt;</c> header
/// Banyan's middleware expects. Keep in sync with the matching middleware in Banyan.Web.
/// </summary>
public static class NidAuthHeader
{
    /// <summary>
    /// NPS wire frames (IdentFrame, RevokeFrame, …) ship with snake_case JSON property names,
    /// so we serialize/deserialize the same way to round-trip cleanly. Case-insensitive on the
    /// read side covers PascalCase variants that older clients might still emit.
    /// </summary>
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Encode an <see cref="IdentFrame"/> as the value of an <c>Authorization</c> header (including the scheme).</summary>
    public static string Build(IdentFrame frame)
    {
        var json   = JsonSerializer.Serialize(frame, s_json);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"{NidAuthenticationOptions.AuthScheme} {base64}";
    }

    /// <summary>Try to decode the header back into an <see cref="IdentFrame"/>. Returns null on any malformed input.</summary>
    public static IdentFrame? TryParse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        var prefix = NidAuthenticationOptions.AuthScheme + " ";
        if (!headerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = headerValue[prefix.Length..].Trim();
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            return JsonSerializer.Deserialize<IdentFrame>(json, s_json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Apply the header to an <see cref="HttpClient"/> (sets <c>DefaultRequestHeaders.Authorization</c>).</summary>
    public static void ApplyTo(HttpClient http, IdentFrame frame)
    {
        var json   = JsonSerializer.Serialize(frame, s_json);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(NidAuthenticationOptions.AuthScheme, base64);
    }
}
