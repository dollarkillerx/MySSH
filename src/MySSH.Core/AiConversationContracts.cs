namespace MySSH.Core;

/// <summary>An AI assistant conversation (design §7.7). The title (first user words) is stored encrypted.</summary>
public sealed record AiConversation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; init; }
    /// <summary>The provider configuration last used (null once that provider was deleted).</summary>
    public Guid? ProviderConfigId { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}

/// <summary>One history entry, opaque to storage: <paramref name="PayloadJson"/> is the serialized MySSH.Ai message
/// (encrypted at rest). <paramref name="Seq"/> is 0-based and contiguous per conversation (history is append-only).</summary>
public sealed record AiStoredMessage(int Seq, int Role, string PayloadJson, DateTimeOffset CreatedAt);

/// <summary>Thrown when a stored conversation can no longer be decrypted (e.g. data from another vault).</summary>
public sealed class AiConversationUnreadableException : Exception
{
    public AiConversationUnreadableException() { }
    public AiConversationUnreadableException(string message) : base(message) { }
    public AiConversationUnreadableException(string message, Exception inner) : base(message, inner) { }
}

public interface IAiConversationStore
{
    /// <summary>Newest first. Conversations whose title cannot be decrypted are listed with an empty title.</summary>
    Task<IReadOnlyList<AiConversation>> ListAsync(CancellationToken ct = default);

    /// <summary>Insert or update (title, provider, model, updated time, token counters).</summary>
    Task SaveAsync(AiConversation conversation, CancellationToken ct = default);

    /// <summary>Appends one message. Throws <see cref="InvalidOperationException"/> if <see cref="AiStoredMessage.Seq"/> is
    /// taken (history is never rewritten) or the conversation does not exist.</summary>
    Task AppendMessageAsync(Guid conversationId, AiStoredMessage message, CancellationToken ct = default);

    /// <summary>In <see cref="AiStoredMessage.Seq"/> order. Throws <see cref="AiConversationUnreadableException"/>.</summary>
    Task<IReadOnlyList<AiStoredMessage>> GetMessagesAsync(Guid conversationId, CancellationToken ct = default);

    Task<bool> DeleteAsync(Guid conversationId, CancellationToken ct = default);

    Task<int> DeleteAllAsync(CancellationToken ct = default);

    /// <summary>Deletes conversations last updated before <paramref name="cutoff"/> ("delete after N days").</summary>
    Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
