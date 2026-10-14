namespace Concierge.Shared.Chat;

/// <summary>A transport/service failure, not an assistant answer or refusal.</summary>
public sealed class ChatRuntimeUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
