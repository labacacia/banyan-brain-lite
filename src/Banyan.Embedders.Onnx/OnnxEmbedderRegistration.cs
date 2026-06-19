// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Embedders;

/// <summary>
/// Wires the ONNX embedder into the core <see cref="EmbedderFactory"/> (KB-8 split).
/// A top-level host that wants semantic embeddings references the
/// <c>Banyan.Embedders.Onnx</c> package and calls <see cref="Register"/> once at
/// startup; hosts that don't stay on the hashing fallback and never pull the ONNX
/// runtime. Explicit (rather than a module initializer) so activation is
/// deterministic and the core has no hidden dependency on load order.
/// </summary>
public static class OnnxEmbedderRegistration
{
    /// <summary>Registers <see cref="OnnxEmbedder"/> as the factory's ONNX provider. Idempotent.</summary>
    public static void Register()
        => EmbedderFactory.OnnxProvider = static (opts, _) => OnnxEmbedder.Open(opts);
}
