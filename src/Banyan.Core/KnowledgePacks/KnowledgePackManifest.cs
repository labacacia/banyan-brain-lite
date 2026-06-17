using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banyan.Core.KnowledgePacks;

public sealed record KnowledgePackManifest
{
    public const string CurrentSchemaVersion = "banyan.pack.manifest.v1";

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Pack format generation (KB-2). 1 = legacy (no in-pack embeddings/signature),
    /// 2 = signed + embeddings + provenance. Absent in v1 packs → treated as 1.</summary>
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; init; } = 1;

    /// <summary>Embedder profile the in-pack embeddings were produced with (KB-2),
    /// e.g. <c>bge-small-zh-v1.5</c>. Used for mount-time compatibility checks.</summary>
    [JsonPropertyName("embedder_profile")]
    public string? EmbedderProfile { get; init; }

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

    /// <summary>
    /// Present only on encrypted packs. The outer archive keeps this in plaintext so
    /// callers can read algorithm/KDF parameters without a passphrase.
    /// </summary>
    [JsonPropertyName("encryption")]
    public KnowledgePackEncryptionMetadata? Encryption { get; init; }
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

public sealed record KnowledgePackEncryptionMetadata
{
    /// <summary>"aes-256-gcm"</summary>
    [JsonPropertyName("algorithm")]
    public required string Algorithm { get; init; }

    /// <summary>"pbkdf2-sha256"</summary>
    [JsonPropertyName("kdf")]
    public required string Kdf { get; init; }

    /// <summary>Base64-encoded random salt used for key derivation.</summary>
    [JsonPropertyName("salt")]
    public required string Salt { get; init; }

    /// <summary>PBKDF2 iteration count.</summary>
    [JsonPropertyName("iterations")]
    public required int Iterations { get; init; }
}
