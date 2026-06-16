namespace Banyan.Core.Isolation;

/// <summary>
/// Resolves a <see cref="RequestEnvelope"/> into an <see cref="IsolationContext"/>.
/// Each edition supplies its own implementation:
/// <list type="bullet">
///   <item>Lite — reads capabilities from the NID frame; tenant/org/workspace are placeholders (ISO-4).</item>
///   <item>Pro  — derives org/workspace from the OrgNid (ISO-6).</item>
///   <item>Ent  — validates the signed envelope and produces the full chain (ISO-7).</item>
/// </list>
/// </summary>
public interface IIsolationContextResolver
{
    ValueTask<IsolationContext> ResolveAsync(RequestEnvelope request, CancellationToken ct = default);
}
