using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banyan.Core.KnowledgePacks;

public sealed record KnowledgePackManifest
{
    public const string CurrentSchemaVersion = "banyan.pack.manifest.v1";

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("pack_id")]
    public required string PackId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("publisher")]
    public string? Publisher { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("valid_from")]
    public DateTimeOffset? ValidFrom { get; init; }

    [JsonPropertyName("valid_until")]
    public DateTimeOffset? ValidUntil { get; init; }

    [JsonPropertyName("pack_type")]
    public required string PackType { get; init; }

    [JsonPropertyName("content_types")]
    public required IReadOnlyList<string> ContentTypes { get; init; }

    [JsonPropertyName("target_scopes")]
    public required IReadOnlyList<string> TargetScopes { get; init; }

    [JsonPropertyName("permissions")]
    public KnowledgePackPermissions Permissions { get; init; } = new();

    [JsonPropertyName("indexes")]
    public KnowledgePackIndexes Indexes { get; init; } = new();

    [JsonPropertyName("checksums")]
    public IReadOnlyDictionary<string, string> Checksums { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record KnowledgePackPermissions
{
    [JsonPropertyName("allow_recall")]
    public bool AllowRecall { get; init; } = true;

    [JsonPropertyName("allow_export")]
    public bool AllowExport { get; init; }

    [JsonPropertyName("allow_finetune")]
    public bool AllowFinetune { get; init; }
}

public sealed record KnowledgePackIndexes
{
    [JsonPropertyName("keyword")]
    public bool Keyword { get; init; } = true;

    [JsonPropertyName("vector")]
    public bool Vector { get; init; }

    [JsonPropertyName("graph")]
    public bool Graph { get; init; }
}
