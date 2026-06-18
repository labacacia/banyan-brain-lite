namespace Banyan.Embedders;

/// <summary>Path helpers shared by the embedder core, CLI, and the ONNX companion.</summary>
public static class EmbedderPaths
{
    /// <summary>Expands a leading <c>~</c> to the user profile directory.</summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length == 1 ? home : Path.Combine(home, path.TrimStart('~', '/'));
    }
}
