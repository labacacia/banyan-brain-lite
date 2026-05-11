using System.Text.Json;

namespace Banyan.Core;

public sealed record WriteRequest(
    string Content,
    string Namespace = "default",
    JsonDocument? Metadata = null,
    string? AgentNid = null
);

public sealed record UpdateRequest(
    string Content,
    JsonDocument? Metadata = null,
    string? AgentNid = null
);

public sealed record SearchQuery(
    string Text,
    int K = 10,
    string? Namespace = null,
    SearchMode Mode = SearchMode.Hybrid
);

public enum SearchMode { Hybrid, Vector, Lexical }

public sealed record SearchHit(
    Memory Memory,
    double Score,
    int? VectorRank,
    int? LexicalRank
);
