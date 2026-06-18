// Copyright 2026 INNO LOTUS PTY LTD
// SPDX-License-Identifier: Apache-2.0

namespace Banyan.Core.KnowledgePacks;

public sealed class KnowledgePackWrongPassphraseException(string message, Exception? inner = null)
    : Exception(message, inner);
