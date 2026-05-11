using System.Text.Json;
using Banyan.Auth;

namespace Banyan.Cli.Commands;

/// <summary>Helpers for loading <see cref="BanyanNipCaOptions"/> + resolving the CA passphrase.</summary>
internal static class CaContext
{
    public const string DefaultConfigPath = "~/.banyan/ca-config.json";
    public const string PassphraseEnvVar  = "BANYAN_NIP_CA_PASSPHRASE";

    public static BanyanNipCaOptions LoadOptions(string? configPathArg)
    {
        var path = CommandContext.ExpandHome(configPathArg
            ?? Environment.GetEnvironmentVariable("BANYAN_CA_CONFIG")
            ?? DefaultConfigPath);

        var opts = File.Exists(path)
            ? JsonSerializer.Deserialize<BanyanNipCaOptions>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            : new BanyanNipCaOptions();

        // Passphrase never lives in the config file. Resolution order: --passphrase > env var > prompt.
        return opts;
    }

    /// <summary>Return the passphrase from CLI flag → env var → interactive prompt. Returns null if interactive prompt is suppressed and no source is available.</summary>
    public static string? ResolvePassphrase(string[] args, bool allowPrompt = true)
    {
        var fromArg = CommandContext.GetOption(args, "--passphrase");
        if (!string.IsNullOrEmpty(fromArg)) return fromArg;

        var fromEnv = Environment.GetEnvironmentVariable(PassphraseEnvVar);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        if (!allowPrompt) return null;

        Console.Write("CA key passphrase: ");
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
