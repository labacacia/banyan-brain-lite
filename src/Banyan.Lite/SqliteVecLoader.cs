// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;

namespace Banyan.Lite;

/// <summary>
/// Loads the <a href="https://github.com/asg017/sqlite-vec">sqlite-vec</a> extension
/// (<c>vec0</c> virtual table + <c>vec_f32</c> codec) into an open <see cref="SqliteConnection"/>.
/// Cross-platform path discovery: explicit override → env var <c>BANYAN_SQLITE_VEC_LIB</c> →
/// well-known names next to the entry-point assembly → user-profile cache.
/// </summary>
public static class SqliteVecLoader
{
    public const string EnvVar     = "BANYAN_SQLITE_VEC_LIB";
    public const string DefaultDir = "~/.banyan/sqlite-vec";

    /// <summary>True after a successful <see cref="TryLoad"/> on this connection.</summary>
    public static bool TryLoad(SqliteConnection conn, string? explicitPath = null)
    {
        var path = ResolvePath(explicitPath);
        if (path is null) return false;

        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(path);
            // Sanity probe: vec_version() returns the sqlite-vec build string.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT vec_version()";
            _ = cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? ResolvePath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            var ep = ExpandHome(explicitPath);
            return File.Exists(ep) ? ep : null;
        }

        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } env)
        {
            var p = ExpandHome(env);
            if (File.Exists(p)) return p;
        }

        var libName = LibraryName();
        var candidates = new[]
        {
            Path.Combine(ExpandHome(DefaultDir), libName),
            Path.Combine(AppContext.BaseDirectory, libName),
            Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeId(), "native", libName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string LibraryName()
    {
        // sqlite-vec ships as `vec0.dylib` on macOS, `vec0.dll` on Windows, `vec0.so` on Linux.
        if (OperatingSystem.IsMacOS())   return "vec0.dylib";
        if (OperatingSystem.IsWindows()) return "vec0.dll";
        return "vec0.so";
    }

    private static string RuntimeId()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => "x64",
            };
        if (OperatingSystem.IsMacOS())   return $"osx-{arch}";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        return $"linux-{arch}";
    }

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
