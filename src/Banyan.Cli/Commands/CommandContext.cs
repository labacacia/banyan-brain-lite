// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Banyan.Identity;

namespace Banyan.Cli.Commands;

/// <summary>Shared helpers across CLI commands.</summary>
internal static class CommandContext
{
    public const string DefaultConfigPath  = "~/.banyan/identity-config.json";
    public const string DefaultTokensPath  = "~/.banyan/tokens.json";

    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }

    public static BanyanIdentityOptions LoadOptions(string? configPathArg)
    {
        var path = ExpandHome(configPathArg
            ?? Environment.GetEnvironmentVariable("BANYAN_IDENTITY_CONFIG")
            ?? DefaultConfigPath);

        if (!File.Exists(path))
        {
            // Defaults are usable for keygen / first-time init even without a config file.
            return new BanyanIdentityOptions();
        }

        var json = File.ReadAllText(path);
        var opts = JsonSerializer.Deserialize<BanyanIdentityOptions>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new BanyanIdentityOptions();
        return opts;
    }

    public static string? GetOption(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    public static IEnumerable<string> GetOptions(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) yield return args[i + 1];
    }

    public static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    public static string Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine() ?? "";
    }

    public static string PromptSecret(string label)
    {
        Console.Write(label);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (k.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        return sb.ToString();
    }
}

/// <summary>Cached token bundle written to <see cref="CommandContext.DefaultTokensPath"/> after a successful login.</summary>
internal sealed class TokenCache
{
    public string  AccessToken     { get; set; } = "";
    public string? RefreshToken    { get; set; }
    public string  Issuer          { get; set; } = "";
    public string  ClientId        { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }

    public static TokenCache? TryLoad()
    {
        var path = CommandContext.ExpandHome(CommandContext.DefaultTokensPath);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<TokenCache>(File.ReadAllText(path));
    }

    public void Save()
    {
        var path = CommandContext.ExpandHome(CommandContext.DefaultTokensPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void Clear()
    {
        var path = CommandContext.ExpandHome(CommandContext.DefaultTokensPath);
        if (File.Exists(path)) File.Delete(path);
    }
}
