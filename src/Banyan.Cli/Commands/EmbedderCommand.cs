using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Banyan.Embedders;
using Banyan.Lite;

namespace Banyan.Cli.Commands;

/// <summary>Sub-dispatcher for <c>banyan embedder &lt;subcommand&gt;</c>.</summary>
internal static class EmbedderCommand
{
    private const string DefaultModelUrl =
        "https://huggingface.co/Xenova/bge-small-zh-v1.5/resolve/main/onnx/model_quantized.onnx";
    private const string DefaultVocabUrl =
        "https://huggingface.co/Xenova/bge-small-zh-v1.5/resolve/main/vocab.txt";
    private const string SqliteVecVersion  = "v0.1.9";
    private const string SqliteVecBaseUrl  =
        "https://github.com/asg017/sqlite-vec/releases/download/v0.1.9";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { Help(); return 64; }
        var sub = args[0];
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "download" => await DownloadAsync(rest),
            "info"     => Info(rest),
            "--help" or "-h" or "help" => Help(),
            _          => Unknown(sub),
        };
    }

    private static async Task<int> DownloadAsync(string[] args)
    {
        var opts = new OnnxEmbedderOptions();
        var modelPath = OnnxEmbedder.ExpandHome(CommandContext.GetOption(args, "--model-out") ?? opts.ModelPath);
        var vocabPath = OnnxEmbedder.ExpandHome(CommandContext.GetOption(args, "--vocab-out") ?? opts.VocabPath);
        var modelUrl  = CommandContext.GetOption(args, "--model-url") ?? DefaultModelUrl;
        var vocabUrl  = CommandContext.GetOption(args, "--vocab-url") ?? DefaultVocabUrl;
        var force     = CommandContext.HasFlag(args, "--force");
        var withVec   = !CommandContext.HasFlag(args, "--no-vec");

        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(vocabPath)!);

        await DownloadOneAsync(modelUrl, modelPath, "model.onnx", force);
        await DownloadOneAsync(vocabUrl, vocabPath, "vocab.txt",  force);

        string? vecLibPath = null;
        if (withVec) vecLibPath = await DownloadVecAsync(force);

        Console.WriteLine();
        Console.WriteLine("Done. Set the embedder for new processes:");
        Console.WriteLine($"  export BANYAN_EMBEDDER=onnx");
        Console.WriteLine($"  export BANYAN_EMBEDDER_MODEL={modelPath}");
        Console.WriteLine($"  export BANYAN_EMBEDDER_VOCAB={vocabPath}");
        if (vecLibPath is not null)
            Console.WriteLine($"  export BANYAN_SQLITE_VEC_LIB={vecLibPath}");
        return 0;
    }

    /// <summary>Pull the sqlite-vec loadable extension for this OS+arch and unpack into the default cache dir.</summary>
    private static async Task<string?> DownloadVecAsync(bool force)
    {
        var (assetName, libName) = ResolveVecAsset();
        if (assetName is null)
        {
            Console.WriteLine($"[skip]   sqlite-vec: no prebuilt for {RuntimeInformation.OSDescription} / {RuntimeInformation.OSArchitecture} — install manually if needed");
            return null;
        }

        var dstDir = Path.Combine(OnnxEmbedder.ExpandHome("~/.banyan/sqlite-vec"));
        Directory.CreateDirectory(dstDir);
        var libPath = Path.Combine(dstDir, libName);
        if (File.Exists(libPath) && !force)
        {
            Console.WriteLine($"[skip]   sqlite-vec already at {libPath} (use --force to redownload)");
            return libPath;
        }

        var url = $"{SqliteVecBaseUrl}/{assetName}";
        var tarballPath = Path.Combine(Path.GetTempPath(), assetName);
        await DownloadOneAsync(url, tarballPath, $"sqlite-vec ({SqliteVecVersion})", force: true);

        // Extract just the library out of the tarball.
        await using (var fs  = File.OpenRead(tarballPath))
        await using (var gz  = new GZipStream(fs, CompressionMode.Decompress))
        await using (var tar = new TarReader(gz))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (entry.Name.EndsWith(libName, StringComparison.OrdinalIgnoreCase))
                {
                    await using var dst = File.Create(libPath);
                    if (entry.DataStream is null) continue;
                    await entry.DataStream.CopyToAsync(dst);
                    break;
                }
            }
        }
        File.Delete(tarballPath);

        if (!File.Exists(libPath))
        {
            Console.Error.WriteLine($"[error]  could not extract {libName} from {assetName}");
            return null;
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(libPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Console.WriteLine($"[done]   sqlite-vec → {libPath}");
        return libPath;
    }

    private static (string? Asset, string LibName) ResolveVecAsset()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64   => "x86_64",
            _                  => null,
        };
        if (arch is null) return (null, "");

        if (OperatingSystem.IsLinux())
            return ($"sqlite-vec-{SqliteVecVersion[1..]}-loadable-linux-{arch}.tar.gz", "vec0.so");
        if (OperatingSystem.IsMacOS())
            return ($"sqlite-vec-{SqliteVecVersion[1..]}-loadable-macos-{arch}.tar.gz", "vec0.dylib");
        if (OperatingSystem.IsWindows())
            return ($"sqlite-vec-{SqliteVecVersion[1..]}-loadable-windows-{arch}.tar.gz", "vec0.dll");
        return (null, "");
    }

    private static async Task DownloadOneAsync(string url, string dst, string label, bool force)
    {
        if (File.Exists(dst) && !force)
        {
            Console.WriteLine($"[skip]   {label} already at {dst} (use --force to redownload)");
            return;
        }
        Console.Write($"[fetch]  {label} ← {url} … ");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var fs  = File.Create(dst))
        {
            var buf = new byte[81920];
            long copied = 0; int n;
            var lastReport = DateTime.UtcNow;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n));
                copied += n;
                if ((DateTime.UtcNow - lastReport).TotalSeconds > 0.5 && total > 0)
                {
                    Console.Write($"\r[fetch]  {label}  {copied / 1024 / 1024} / {total / 1024 / 1024} MB ");
                    lastReport = DateTime.UtcNow;
                }
            }
        }
        Console.WriteLine($"\r[done]   {label} → {dst} ({new FileInfo(dst).Length} bytes){new string(' ', 20)}");
    }

    private static int Info(string[] args)
    {
        var opts = new OnnxEmbedderOptions();
        if (CommandContext.GetOption(args, "--model") is { } m) opts.ModelPath = m;
        if (CommandContext.GetOption(args, "--vocab") is { } v) opts.VocabPath = v;

        var mp = OnnxEmbedder.ExpandHome(opts.ModelPath);
        var vp = OnnxEmbedder.ExpandHome(opts.VocabPath);
        Console.WriteLine($"model: {mp}    {(File.Exists(mp) ? $"OK ({new FileInfo(mp).Length / 1024 / 1024} MB)" : "MISSING")}");
        Console.WriteLine($"vocab: {vp}    {(File.Exists(vp) ? $"OK ({new FileInfo(vp).Length / 1024} KB)" : "MISSING")}");

        if (File.Exists(mp) && File.Exists(vp))
        {
            try
            {
                using var emb = OnnxEmbedder.Open(opts);
                Console.WriteLine($"dim:      {emb.Dimensions}");
                Console.WriteLine($"model_id: {emb.ModelId}");
                var sample = emb.EmbedAsync("hello world").GetAwaiter().GetResult();
                Console.WriteLine($"sample:   {sample.Length} dims, first 5 = [{string.Join(", ", sample.Take(5).Select(x => x.ToString("F4")))}]");
            }
            catch (Exception ex) { Console.WriteLine($"load error: {ex.Message}"); }
        }
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            banyan embedder <subcommand>
              download   Pull the bge-small-zh-v1.5 ONNX model + BERT WordPiece vocab (~24 MB)
                         and the sqlite-vec loadable extension (~60 KB).
                           --model-out PATH    (default: ~/.banyan/embedder/model.onnx)
                           --vocab-out PATH    (default: ~/.banyan/embedder/vocab.txt)
                           --model-url URL     (default: HuggingFace Xenova/bge-small-zh-v1.5)
                           --vocab-url URL
                           --no-vec            skip the sqlite-vec extension (use linear scan)
                           --force             redownload existing files
              info       Show paths / sizes / load status / sample embedding
            """);
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"banyan embedder: unknown subcommand '{sub}'. Run `banyan embedder --help`.");
        return 64;
    }
}
