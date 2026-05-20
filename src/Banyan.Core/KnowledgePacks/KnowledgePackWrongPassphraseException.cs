namespace Banyan.Core.KnowledgePacks;

public sealed class KnowledgePackWrongPassphraseException(string message, Exception? inner = null)
    : Exception(message, inner);
