// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Banyan.Web;

public static class WebConfigStore
{
    public const string DefaultPath = "~/.banyan/web-config.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static WebOptions Load(string? path = null)
    {
        var resolved = WebOptions.ExpandHome(path ?? DefaultPath);
        if (!File.Exists(resolved))
            return new WebOptions();

        return JsonSerializer.Deserialize<WebOptions>(
            File.ReadAllText(resolved),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new WebOptions();
    }

    public static void Save(WebOptions opts, string? path = null)
    {
        var resolved = WebOptions.ExpandHome(path ?? DefaultPath);
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(resolved, JsonSerializer.Serialize(opts, s_json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(resolved, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
