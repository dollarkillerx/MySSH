using System.Security.Cryptography;
using MySSH.Core;

namespace MySSH.Storage;

/// <summary>
/// Tables <c>ai_conversations</c> / <c>ai_messages</c>. Titles and payloads are encrypted with the vault master key; a
/// message's AAD binds it to its conversation and position, so blobs cannot be moved or reordered. Not part of backups.
/// </summary>
internal sealed class AiConversationStore(StorageDatabase db, Vault vault) : IAiConversationStore
{
    public const string ConversationsTable = "ai_conversations";
    public const string MessagesTable = "ai_messages";
    public const string FieldTitle = "title";
    public const string FieldPayload = "payload";

    internal static Guid MessageId(Guid conversationId, int seq) =>
        DeterministicGuid.Create(FormattableString.Invariant($"ai-message:{conversationId:D}:{seq}"));

    public async Task<IReadOnlyList<AiConversation>> ListAsync(CancellationToken ct = default)
    {
        vault.EnsureUnlocked();
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Sql.Command(conn, null, """
            SELECT id, title, provider_config_id, model, created_at, updated_at, input_tokens, output_tokens
            FROM ai_conversations ORDER BY updated_at DESC, id;
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AiConversation>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = Guid.Parse(r.GetString(0));
            string title;
            try
            {
                title = vault.Decrypt(ConversationsTable, id, FieldTitle, (byte[])r.GetValue(1));
            }
            catch (CryptographicException)
            {
                title = "";
            }
            list.Add(new AiConversation
            {
                Id = id,
                Title = title,
                ProviderConfigId = Sql.GuidOrNull(r, 2),
                Model = Sql.StringOrNull(r, 3),
                CreatedAt = Sql.ParseTime(r.GetString(4)),
                UpdatedAt = new DateTimeOffset(r.GetInt64(5), TimeSpan.Zero),
                InputTokens = r.GetInt64(6),
                OutputTokens = r.GetInt64(7),
            });
        }
        return list;
    }

    public async Task SaveAsync(AiConversation conversation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        vault.EnsureUnlocked();
        var title = vault.Encrypt(ConversationsTable, conversation.Id, FieldTitle, conversation.Title);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, null, """
            INSERT INTO ai_conversations (id, title, provider_config_id, model, created_at, updated_at, input_tokens, output_tokens)
            VALUES ($id, $title, $provider, $model, $created, $updated, $in, $out)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, provider_config_id = excluded.provider_config_id,
                model = excluded.model, updated_at = excluded.updated_at, input_tokens = excluded.input_tokens,
                output_tokens = excluded.output_tokens;
            """, ct,
            ("$id", Sql.Id(conversation.Id)), ("$title", title), ("$provider", Sql.Id(conversation.ProviderConfigId)),
            ("$model", conversation.Model), ("$created", Sql.Time(conversation.CreatedAt)), ("$updated", conversation.UpdatedAt.UtcTicks),
            ("$in", conversation.InputTokens), ("$out", conversation.OutputTokens)).ConfigureAwait(false);
    }

    public async Task AppendMessageAsync(Guid conversationId, AiStoredMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(message.Seq);
        vault.EnsureUnlocked();
        var blob = vault.Encrypt(MessagesTable, MessageId(conversationId, message.Seq), FieldPayload, message.PayloadJson);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        if (!await Sql.ExistsAsync(conn, tx, "SELECT 1 FROM ai_conversations WHERE id = $id;", ct, ("$id", Sql.Id(conversationId))).ConfigureAwait(false))
            throw new InvalidOperationException($"AI conversation {conversationId} does not exist.");
        try
        {
            await Sql.ExecAsync(conn, tx, """
                INSERT INTO ai_messages (conversation_id, seq, role, payload, created_at) VALUES ($c, $seq, $role, $payload, $t);
                """, ct, ("$c", Sql.Id(conversationId)), ("$seq", message.Seq), ("$role", message.Role), ("$payload", blob),
                ("$t", Sql.Time(message.CreatedAt))).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new InvalidOperationException($"Message {message.Seq} of AI conversation {conversationId} already exists; history is append-only.", e);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AiStoredMessage>> GetMessagesAsync(Guid conversationId, CancellationToken ct = default)
    {
        vault.EnsureUnlocked();
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = Sql.Command(conn, null,
            "SELECT seq, role, payload, created_at FROM ai_messages WHERE conversation_id = $c ORDER BY seq;", ("$c", Sql.Id(conversationId)));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AiStoredMessage>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var seq = r.GetInt32(0);
            if (seq != list.Count)
                throw new AiConversationUnreadableException($"AI conversation {conversationId} is missing message {list.Count}.");
            string payload;
            try
            {
                payload = vault.Decrypt(MessagesTable, MessageId(conversationId, seq), FieldPayload, (byte[])r.GetValue(2));
            }
            catch (CryptographicException e)
            {
                throw new AiConversationUnreadableException($"AI conversation {conversationId} could not be decrypted.", e);
            }
            list.Add(new AiStoredMessage(seq, r.GetInt32(1), payload, Sql.ParseTime(r.GetString(3))));
        }
        return list;
    }

    public async Task<bool> DeleteAsync(Guid conversationId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, tx, "DELETE FROM ai_messages WHERE conversation_id = $c;", ct, ("$c", Sql.Id(conversationId))).ConfigureAwait(false);
        var n = await Sql.ExecAsync(conn, tx, "DELETE FROM ai_conversations WHERE id = $c;", ct, ("$c", Sql.Id(conversationId))).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return n > 0;
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, tx, "DELETE FROM ai_messages;", ct).ConfigureAwait(false);
        var n = await Sql.ExecAsync(conn, tx, "DELETE FROM ai_conversations;", ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return n;
    }

    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await Sql.BeginAsync(conn, ct).ConfigureAwait(false);
        await Sql.ExecAsync(conn, tx, """
            DELETE FROM ai_messages WHERE conversation_id IN (SELECT id FROM ai_conversations WHERE updated_at < $cut);
            """, ct, ("$cut", cutoff.UtcTicks)).ConfigureAwait(false);
        var n = await Sql.ExecAsync(conn, tx, "DELETE FROM ai_conversations WHERE updated_at < $cut;", ct, ("$cut", cutoff.UtcTicks)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return n;
    }
}
