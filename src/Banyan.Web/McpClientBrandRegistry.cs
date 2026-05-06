// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Banyan.Web;

internal sealed record McpClientBrand(string? Name, string? Version, DateTimeOffset LastSeenAt)
{
    public string? DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
                return null;
            return string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
        }
    }
}

internal sealed class McpClientBrandRegistry(LocalAgentIdentity localAgent, WebOptions opts)
{
    private readonly object _gate = new();
    private readonly string _path = WebOptions.ExpandHome(opts.LocalAgentBrandPath);
    private McpClientBrand? _cached;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string? LocalAgentDisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(localAgent.Nid))
                return null;

            var brand = Load();
            return brand?.DisplayName;
        }
    }

    public void Seen(string? name, string? version)
    {
        if (string.IsNullOrWhiteSpace(localAgent.Nid) || string.IsNullOrWhiteSpace(name))
            return;

        var brand = new McpClientBrand(name.Trim(), version?.Trim(), DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _cached = brand;
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(brand, s_json));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private McpClientBrand? Load()
    {
        lock (_gate)
        {
            if (_cached is not null)
                return _cached;
            if (!File.Exists(_path))
                return null;

            try
            {
                _cached = JsonSerializer.Deserialize<McpClientBrand>(
                    File.ReadAllText(_path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return _cached;
            }
            catch
            {
                return null;
            }
        }
    }
}
