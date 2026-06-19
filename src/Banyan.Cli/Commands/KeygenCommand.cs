// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Banyan.Identity.Crypto;

namespace Banyan.Cli.Commands;

internal static class KeygenCommand
{
    public static int Run(string[] args)
    {
        var opts = CommandContext.LoadOptions(CommandContext.GetOption(args, "--config"));
        var outPath = CommandContext.ExpandHome(
            CommandContext.GetOption(args, "--out") ?? opts.SigningKeyPath);
        var bits  = int.TryParse(CommandContext.GetOption(args, "--bits"), out var b) ? b : 2048;
        var force = CommandContext.HasFlag(args, "--force");

        try
        {
            PemSigningKeyLoader.Generate(outPath, bits, force);
            var (key, _) = PemSigningKeyLoader.Load(outPath);
            Console.WriteLine($"Wrote {bits}-bit RSA private key to {outPath}");
            Console.WriteLine($"  kid: {key.KeyId}");
            return 0;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"keygen: {ex.Message}");
            return 2;
        }
    }
}
