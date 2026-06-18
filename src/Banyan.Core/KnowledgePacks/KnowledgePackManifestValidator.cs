// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace Banyan.Core.KnowledgePacks;

public static partial class KnowledgePackManifestValidator
{
    public static KnowledgePackValidationResult Validate(KnowledgePackManifest? manifest)
    {
        var errors = new List<string>();

        if (manifest is null)
        {
            errors.Add("manifest is required");
            return new KnowledgePackValidationResult(errors);
        }

        Require(
            manifest.SchemaVersion == KnowledgePackManifest.CurrentSchemaVersion,
            $"schema_version must be {KnowledgePackManifest.CurrentSchemaVersion}",
            errors);
        RequireNotBlank(manifest.PackId, "pack_id", errors);
        RequireNotBlank(manifest.Name, "name", errors);
        RequireNotBlank(manifest.Version, "version", errors);
        RequireNotBlank(manifest.PackType, "pack_type", errors);
        Require(manifest.CreatedAt != default, "created_at is required", errors);

        if (!string.IsNullOrWhiteSpace(manifest.PackId))
        {
            Require(PackIdRegex().IsMatch(manifest.PackId), "pack_id must use lowercase letters, digits, dots, underscores, or hyphens", errors);
        }

        RequireNonEmptyStrings(manifest.ContentTypes, "content_types", errors);
        RequireNonEmptyStrings(manifest.TargetScopes, "target_scopes", errors);

        if (manifest.ValidFrom.HasValue && manifest.ValidUntil.HasValue)
        {
            Require(manifest.ValidUntil > manifest.ValidFrom, "valid_until must be later than valid_from", errors);
        }

        ValidateMapKeys(manifest.Checksums, "checksums", errors);
        ValidateMapKeys(manifest.Extensions, "extensions", errors);

        return new KnowledgePackValidationResult(errors);
    }

    private static void Require(bool condition, string message, List<string> errors)
    {
        if (!condition)
        {
            errors.Add(message);
        }
    }

    private static void RequireNotBlank(string? value, string field, List<string> errors)
        => Require(!string.IsNullOrWhiteSpace(value), $"{field} is required", errors);

    private static void RequireNonEmptyStrings(IReadOnlyList<string>? values, string field, List<string> errors)
    {
        if (values is null || values.Count == 0)
        {
            errors.Add($"{field} must contain at least one value");
            return;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(values[i]))
            {
                errors.Add($"{field}[{i}] must not be blank");
            }
        }
    }

    private static void ValidateMapKeys<T>(IReadOnlyDictionary<string, T>? values, string field, List<string> errors)
    {
        if (values is null)
        {
            return;
        }

        foreach (var key in values.Keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add($"{field} keys must not be blank");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PackIdRegex();
}

public sealed record KnowledgePackValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new KnowledgePackValidationException(Errors);
        }
    }
}

public sealed class KnowledgePackValidationException : Exception
{
    public KnowledgePackValidationException(IReadOnlyList<string> errors)
        : base("Knowledge pack manifest is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
