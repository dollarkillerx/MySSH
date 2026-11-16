using System.Text;
using MySSH.Core;

namespace MySSH.Storage.Tests;

public sealed class AiConversationStoreTests
{
    [Fact]
    public async Task Conversations_and_messages_round_trip_encrypted()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var store = s.AiConversations;
        var c = new AiConversation { Title = "为什么 nginx 起不来", Model = "claude-opus-5-5", ProviderConfigId = Guid.NewGuid() };
        await store.SaveAsync(c, T.Ct);
        await store.AppendMessageAsync(c.Id, new AiStoredMessage(0, 0, """{"text":"secret-ish payload sk-ant-xyz"}""", DateTimeOffset.UtcNow), T.Ct);
        await store.AppendMessageAsync(c.Id, new AiStoredMessage(1, 1, """{"raw":"[{\"type\":\"thinking\"}]"}""", DateTimeOffset.UtcNow), T.Ct);
        await store.SaveAsync(c with { InputTokens = 120, OutputTokens = 40, UpdatedAt = DateTimeOffset.UtcNow }, T.Ct);

        var listed = Assert.Single(await store.ListAsync(T.Ct));
        Assert.Equal(c.Title, listed.Title);
        Assert.Equal(120, listed.InputTokens);
        var msgs = await store.GetMessagesAsync(c.Id, T.Ct);
        Assert.Equal([0, 1], msgs.Select(m => m.Seq));
        Assert.Contains("sk-ant-xyz", msgs[0].PayloadJson, StringComparison.Ordinal);

        // Nothing readable at rest.
        await using var raw = await T.RawConnectionAsync(s);
        await using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT title FROM ai_conversations UNION ALL SELECT payload FROM ai_messages;";
        await using var r = await cmd.ExecuteReaderAsync(T.Ct);
        while (await r.ReadAsync(T.Ct))
        {
            var text = Encoding.UTF8.GetString((byte[])r.GetValue(0));
            Assert.DoesNotContain("nginx", text, StringComparison.Ordinal);
            Assert.DoesNotContain("sk-ant", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task History_is_append_only_and_bound_to_its_position()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var store = s.AiConversations;
        var c = new AiConversation { Title = "t" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendMessageAsync(c.Id, new AiStoredMessage(0, 0, "{}", DateTimeOffset.UtcNow), T.Ct));
        await store.SaveAsync(c, T.Ct);
        await store.AppendMessageAsync(c.Id, new AiStoredMessage(0, 0, "{\"a\":1}", DateTimeOffset.UtcNow), T.Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendMessageAsync(c.Id, new AiStoredMessage(0, 0, "{\"a\":2}", DateTimeOffset.UtcNow), T.Ct));
        await store.AppendMessageAsync(c.Id, new AiStoredMessage(1, 1, "{\"b\":1}", DateTimeOffset.UtcNow), T.Ct);

        // Swapping two encrypted payloads is detected (AAD binds conversation + seq).
        await using (var raw = await T.RawConnectionAsync(s))
        {
            await using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                UPDATE ai_messages SET payload = (SELECT payload FROM ai_messages m2 WHERE m2.seq = 1 - ai_messages.seq);
                """;
            await cmd.ExecuteNonQueryAsync(T.Ct);
        }
        await Assert.ThrowsAsync<AiConversationUnreadableException>(() => store.GetMessagesAsync(c.Id, T.Ct));
    }

    [Fact]
    public async Task Delete_delete_all_and_prune_by_age()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var store = s.AiConversations;
        var old = new AiConversation { Title = "old", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-40) };
        var recent = new AiConversation { Title = "recent" };
        var other = new AiConversation { Title = "other" };
        foreach (var c in new[] { old, recent, other })
        {
            await store.SaveAsync(c, T.Ct);
            await store.AppendMessageAsync(c.Id, new AiStoredMessage(0, 0, "{}", DateTimeOffset.UtcNow), T.Ct);
        }
        Assert.Equal("old", (await store.ListAsync(T.Ct))[^1].Title); // newest first

        Assert.Equal(1, await store.PruneAsync(DateTimeOffset.UtcNow.AddDays(-30), T.Ct));
        Assert.Empty(await store.GetMessagesAsync(old.Id, T.Ct));
        Assert.True(await store.DeleteAsync(recent.Id, T.Ct));
        Assert.Equal("other", Assert.Single(await store.ListAsync(T.Ct)).Title);
        Assert.Equal(1, await store.DeleteAllAsync(T.Ct));
        Assert.Empty(await store.ListAsync(T.Ct));
    }
}
