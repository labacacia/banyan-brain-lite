namespace Banyan.Lite;

public sealed record RetrievalOptions(
    int RrfK = 60,
    int VectorTopK = 64,
    int LexicalTopK = 64,
    int FinalTopK = 10)
{
    public static RetrievalOptions FromEnvironment() => new(
        RrfK:        EnvInt("BANYAN_RRF_K",         60, min: 1),
        VectorTopK:  EnvInt("BANYAN_VECTOR_TOP_K",  64, min: 1),
        LexicalTopK: EnvInt("BANYAN_LEXICAL_TOP_K", 64, min: 1),
        FinalTopK:   EnvInt("BANYAN_FINAL_TOP_K",   10, min: 1));

    private static int EnvInt(string name, int fallback, int min)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value >= min ? value : fallback;
    }
}
