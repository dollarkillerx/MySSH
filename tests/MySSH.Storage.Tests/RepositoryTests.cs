using System.Text.Json;
using MySSH.Core;

namespace MySSH.Storage.Tests;

public class RepositoryTests
{
    [Fact]
    public async Task Host_round_trips_every_field()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var identity = new Identity { Label = "id", Kind = IdentityKind.Password };
        var aiIdentity = new Identity { Label = "ai", Kind = IdentityKind.Password };
        var proxy = new ProxyProfile { Label = "p", Hostname = "proxy", Port = 8080 };
        var group = new HostGroup { Name = "prod" };
        await s.Identities.SaveAsync(identity, null, T.Ct);
        await s.Identities.SaveAsync(aiIdentity, null, T.Ct);
        await s.Proxies.SaveAsync(proxy, null, T.Ct);
        await s.Groups.SaveAsync(group, T.Ct);
        var jump = T.NewHost("jump");
        await s.Hosts.SaveAsync(jump, T.Ct);

        var host = new Host
        {
            Label = "web · 本番", Hostname = "web.example.com", Port = 2222, Username = "deploy", IdentityId = identity.Id,
            GroupId = group.Id, Tags = ["prod", "web"], ProxyId = proxy.Id, JumpChain = [jump.Id], KeepAliveSeconds = 15,
            Encoding = "euc-jp", AiPolicy = AiPolicy.Ask, AiIdentityId = aiIdentity.Id, Notes = "notes",
            CreatedAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(9)).AddTicks(1234),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await s.Hosts.SaveAsync(host, T.Ct);

        var loaded = await s.Hosts.GetAsync(host.Id, T.Ct);
        Assert.NotNull(loaded);
        Assert.Equal(host.Tags, loaded.Tags);
        Assert.Equal(host.JumpChain, loaded.JumpChain);
        Assert.Equal(host.CreatedAt, loaded.CreatedAt);
        Assert.Equal(host.CreatedAt.Offset, loaded.CreatedAt.Offset);
        Assert.Equal(host with { Tags = loaded.Tags, JumpChain = loaded.JumpChain }, loaded);
        Assert.Equal(2, (await s.Hosts.ListAsync(T.Ct)).Count);

        await s.Hosts.SaveAsync(host with { Label = "web2", Tags = [], JumpChain = [] }, T.Ct);
        loaded = await s.Hosts.GetAsync(host.Id, T.Ct);
        Assert.Equal("web2", loaded!.Label);
        Assert.Empty(loaded.Tags);
        Assert.Empty(loaded.JumpChain);
        Assert.Equal(0L, await T.ScalarAsync(s, "SELECT COUNT(*) FROM host_jumps;"));

        await s.Hosts.DeleteAsync(host.Id, T.Ct);
        Assert.Null(await s.Hosts.GetAsync(host.Id, T.Ct));
        await s.Hosts.DeleteAsync(Guid.NewGuid(), T.Ct); // no-op
    }

    [Fact]
    public async Task Deleting_a_jump_host_in_use_throws_EntityInUseException()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var bastion = T.NewHost("bastion");
        var hop2 = T.NewHost("hop2");
        await s.Hosts.SaveAsync(bastion, T.Ct);
        await s.Hosts.SaveAsync(hop2, T.Ct);
        var a = T.NewHost("a") with { JumpChain = [bastion.Id, hop2.Id] };
        var b = T.NewHost("b") with { JumpChain = [bastion.Id] };
        await s.Hosts.SaveAsync(a, T.Ct);
        await s.Hosts.SaveAsync(b, T.Ct);
        Assert.Equal([bastion.Id, hop2.Id], (await s.Hosts.GetAsync(a.Id, T.Ct))!.JumpChain);

        var ex = await Assert.ThrowsAsync<EntityInUseException>(() => s.Hosts.DeleteAsync(bastion.Id, T.Ct));
        Assert.Equal(new[] { a.Id, b.Id }.Order(), ex.ReferencedBy.Order());
        Assert.NotNull(await s.Hosts.GetAsync(bastion.Id, T.Ct));

        await s.Hosts.DeleteAsync(a.Id, T.Ct);
        await s.Hosts.DeleteAsync(b.Id, T.Ct);
        await s.Hosts.DeleteAsync(bastion.Id, T.Ct);
        Assert.Single(await s.Hosts.ListAsync(T.Ct));
    }

    [Fact]
    public async Task Host_references_are_validated()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var h = T.NewHost("h");
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Hosts.SaveAsync(h with { JumpChain = [Guid.NewGuid()] }, T.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Hosts.SaveAsync(h with { IdentityId = Guid.NewGuid() }, T.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Hosts.SaveAsync(h with { ProxyId = Guid.NewGuid() }, T.Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Hosts.SaveAsync(h with { JumpChain = [h.Id] }, T.Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Hosts.SaveAsync(h with { Port = 0 }, T.Ct));
        Assert.Empty(await s.Hosts.ListAsync(T.Ct));
    }

    [Fact]
    public async Task Identity_crud_with_secrets()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var id = new Identity { Label = "key", Username = "alice", Kind = IdentityKind.PrivateKey };
        await s.Identities.SaveAsync(id, new IdentitySecrets(null, "KEY", "phrase"), T.Ct);
        var loaded = await s.Identities.GetAsync(id.Id, T.Ct);
        Assert.True(loaded!.HasPassphrase);
        Assert.Equal(new IdentitySecrets(null, "KEY", "phrase"), await s.Identities.GetSecretsAsync(id.Id, T.Ct));
        Assert.Equal("IdentitySecrets { *** }", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.ToString());

        var raw = (byte[])(await T.ScalarAsync(s, "SELECT private_key FROM identities WHERE id = $id;", ("$id", id.Id.ToString())))!;
        Assert.True(raw.AsSpan().IndexOf("KEY"u8) < 0); // stored encrypted
        Assert.Equal(1, raw[0]);

        await s.Identities.SaveAsync(id with { Label = "renamed" }, null, T.Ct); // keep secrets
        Assert.Equal("KEY", (await s.Identities.GetSecretsAsync(id.Id, T.Ct))!.PrivateKey);

        await s.Identities.SaveAsync(id, new IdentitySecrets(null, "KEY2", null), T.Ct);
        Assert.False((await s.Identities.GetAsync(id.Id, T.Ct))!.HasPassphrase);
        Assert.Equal(new IdentitySecrets(null, "KEY2", null), await s.Identities.GetSecretsAsync(id.Id, T.Ct));
        Assert.Null(await s.Identities.GetSecretsAsync(Guid.NewGuid(), T.Ct));

        var host = T.NewHost("h") with { IdentityId = id.Id };
        await s.Hosts.SaveAsync(host, T.Ct);
        var ex = await Assert.ThrowsAsync<EntityInUseException>(() => s.Identities.DeleteAsync(id.Id, T.Ct));
        Assert.Equal([host.Id], ex.ReferencedBy);
        await s.Hosts.DeleteAsync(host.Id, T.Ct);
        await s.Identities.DeleteAsync(id.Id, T.Ct);
        Assert.Empty(await s.Identities.ListAsync(T.Ct));
    }

    [Fact]
    public async Task Proxy_crud_password_keep_and_clear()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var p = new ProxyProfile { Label = "socks", Kind = ProxyKind.Socks5, Hostname = "proxy", Port = 1080, Username = "u" };
        await s.Proxies.SaveAsync(p, "secret", T.Ct);
        Assert.True((await s.Proxies.GetAsync(p.Id, T.Ct))!.HasPassword);
        Assert.Equal("secret", await s.Proxies.GetPasswordAsync(p.Id, T.Ct));
        await s.Proxies.SaveAsync(p with { Port = 1081 }, null, T.Ct);
        Assert.Equal("secret", await s.Proxies.GetPasswordAsync(p.Id, T.Ct));
        await s.Proxies.SaveAsync(p, "", T.Ct);
        Assert.False((await s.Proxies.GetAsync(p.Id, T.Ct))!.HasPassword);
        Assert.Null(await s.Proxies.GetPasswordAsync(p.Id, T.Ct));

        var host = T.NewHost("h") with { ProxyId = p.Id };
        await s.Hosts.SaveAsync(host, T.Ct);
        await Assert.ThrowsAsync<EntityInUseException>(() => s.Proxies.DeleteAsync(p.Id, T.Ct));
        await s.Hosts.SaveAsync(host with { ProxyId = null }, T.Ct);
        await s.Proxies.DeleteAsync(p.Id, T.Ct);
        Assert.Empty(await s.Proxies.ListAsync(T.Ct));
    }

    [Fact]
    public async Task Groups_reject_cycles_and_reparent_on_delete()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var root = new HostGroup { Name = "root" };
        var mid = new HostGroup { Name = "mid", ParentId = root.Id, Sort = 1 };
        var leaf = new HostGroup { Name = "leaf", ParentId = mid.Id };
        await s.Groups.SaveAsync(root, T.Ct);
        await s.Groups.SaveAsync(mid, T.Ct);
        await s.Groups.SaveAsync(leaf, T.Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Groups.SaveAsync(root with { ParentId = leaf.Id }, T.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Groups.SaveAsync(new HostGroup { Name = "x", ParentId = Guid.NewGuid() }, T.Ct));
        var host = T.NewHost("h") with { GroupId = mid.Id };
        await s.Hosts.SaveAsync(host, T.Ct);

        await s.Groups.DeleteAsync(mid.Id, T.Ct);
        var groups = await s.Groups.ListAsync(T.Ct);
        Assert.Equal(2, groups.Count);
        Assert.Equal(root.Id, groups.Single(g => g.Id == leaf.Id).ParentId);
        Assert.Equal(root.Id, (await s.Hosts.GetAsync(host.Id, T.Ct))!.GroupId);
    }

    [Fact]
    public async Task Known_hosts_are_keyed_by_host_port_and_key_type()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var first = DateTimeOffset.UtcNow.AddDays(-10);
        var hp = KnownHostEntry.FormatHostPort("example.com", 2222);
        Assert.Equal("[example.com]:2222", hp);
        await s.KnownHosts.UpsertAsync(new KnownHostEntry { HostPort = hp, KeyType = "ssh-ed25519", Sha256Fingerprint = "SHA256:aaa", FirstSeen = first, LastSeen = first }, T.Ct);
        await s.KnownHosts.UpsertAsync(new KnownHostEntry { HostPort = hp, KeyType = "rsa-sha2-512", Sha256Fingerprint = "SHA256:bbb", PublicKeyBase64 = "AAAA" }, T.Ct);
        await s.KnownHosts.UpsertAsync(new KnownHostEntry { HostPort = "example.com", KeyType = "ssh-ed25519", Sha256Fingerprint = "SHA256:ccc" }, T.Ct);
        var now = DateTimeOffset.UtcNow;
        await s.KnownHosts.UpsertAsync(new KnownHostEntry { HostPort = hp, KeyType = "ssh-ed25519", Sha256Fingerprint = "SHA256:aaa2", FirstSeen = now, LastSeen = now }, T.Ct);

        var found = await s.KnownHosts.FindAsync(hp, T.Ct);
        Assert.Equal(2, found.Count);
        var ed = found.Single(f => f.KeyType == "ssh-ed25519");
        Assert.Equal("SHA256:aaa2", ed.Sha256Fingerprint);
        Assert.Equal(first, ed.FirstSeen); // first_seen preserved
        Assert.Equal(now, ed.LastSeen);
        Assert.Equal(3, (await s.KnownHosts.ListAsync(T.Ct)).Count);

        await s.KnownHosts.RemoveAsync(hp, "ssh-ed25519", T.Ct);
        Assert.Single(await s.KnownHosts.FindAsync(hp, T.Ct));
    }

    [Fact]
    public async Task Settings_store_json_values()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        await s.Settings.SetJsonAsync("ui.theme", "\"g100\"", T.Ct);
        Assert.Equal("\"g100\"", await s.Settings.GetJsonAsync("ui.theme", T.Ct));
        await s.Settings.SetAsync("ui.recent", new List<string> { "a", "b" }, StorageJsonContext.Default.ListString, T.Ct);
        Assert.Equal(["a", "b"], await s.Settings.GetAsync("ui.recent", StorageJsonContext.Default.ListString, T.Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Settings.SetJsonAsync("bad", "{nope", T.Ct));
        Assert.Equal(["ui.recent", "ui.theme"], await s.Settings.ListKeysAsync(T.Ct));
        Assert.True(await s.Settings.RemoveAsync("ui.theme", T.Ct));
        Assert.False(await s.Settings.RemoveAsync("ui.theme", T.Ct));
        Assert.Null(await s.Settings.GetJsonAsync("ui.theme", T.Ct));
        using var doc = JsonDocument.Parse((await s.Settings.GetJsonAsync("ui.recent", T.Ct))!);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Undecryptable_record_is_kept_flagged_and_never_overwritten()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var bad = new Identity { Label = "bad", Kind = IdentityKind.Password, Username = "u" };
        var good = new Identity { Label = "good", Kind = IdentityKind.Password };
        await s.Identities.SaveAsync(bad, new IdentitySecrets("pw-bad", null, null), T.Ct);
        await s.Identities.SaveAsync(good, new IdentitySecrets("pw-good", null, null), T.Ct);
        var host = T.NewHost("uses bad") with { IdentityId = bad.Id };
        await s.Hosts.SaveAsync(host, T.Ct);

        // Simulate corruption / a blob encrypted under another key.
        var blob = (byte[])(await T.ScalarAsync(s, "SELECT password FROM identities WHERE id = $id;", ("$id", bad.Id.ToString())))!;
        blob[^1] ^= 0xFF;
        await T.ExecAsync(s, "UPDATE identities SET password = $b WHERE id = $id;", ("$b", blob), ("$id", bad.Id.ToString()));

        var ex = await Assert.ThrowsAsync<SecretUnavailableException>(() => s.Identities.GetSecretsAsync(bad.Id, T.Ct));
        Assert.Equal(("identities", bad.Id, "password"), (ex.Table, ex.RecordId, ex.Field));
        var flagged = Assert.Single(await s.UndecryptableRecords.ListAsync(ct: T.Ct));
        Assert.Equal((bad.Id, "password"), (flagged.RecordId, flagged.Field));

        // Saving OTHER records, and the bad record's metadata, never rewrites the undecryptable blob.
        await s.Identities.SaveAsync(good with { Label = "good2" }, new IdentitySecrets("pw-good2", null, null), T.Ct);
        await s.Identities.SaveAsync(bad with { Label = "bad (renamed)" }, null, T.Ct);
        await s.Hosts.SaveAsync(T.NewHost("another"), T.Ct);
        Assert.Equal(blob, (byte[])(await T.ScalarAsync(s, "SELECT password FROM identities WHERE id = $id;", ("$id", bad.Id.ToString())))!);
        Assert.Equal(2, (await s.Identities.ListAsync(T.Ct)).Count); // not dropped
        Assert.Equal("pw-good2", (await s.Identities.GetSecretsAsync(good.Id, T.Ct))!.Password);

        // Scan finds the same record (idempotent flag).
        Assert.Single(await s.UndecryptableRecords.ScanAsync(T.Ct));

        // The user replaces the secret: flag resolved, a copy of the old blob is retained.
        await s.Identities.SaveAsync(bad, new IdentitySecrets("pw-new", null, null), T.Ct);
        Assert.Equal("pw-new", (await s.Identities.GetSecretsAsync(bad.Id, T.Ct))!.Password);
        Assert.Empty(await s.UndecryptableRecords.ListAsync(ct: T.Ct));
        Assert.NotNull(Assert.Single(await s.UndecryptableRecords.ListAsync(includeResolved: true, T.Ct)).ResolvedAt);
        Assert.Equal(blob, (byte[])(await T.ScalarAsync(s, "SELECT raw_value FROM undecryptable_fields;"))!);
    }

    [Fact]
    public async Task Proxy_password_failure_is_flagged()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var p = new ProxyProfile { Label = "p", Hostname = "h", Port = 1 };
        var other = new ProxyProfile { Label = "o", Hostname = "h", Port = 2 };
        await s.Proxies.SaveAsync(p, "pw", T.Ct);
        await s.Proxies.SaveAsync(other, "pw2", T.Ct);
        // Swap blobs between rows: AAD binding makes both undecryptable.
        await T.ExecAsync(s, "UPDATE proxies SET password = (SELECT password FROM proxies WHERE id = $o) WHERE id = $p;", ("$o", other.Id.ToString()), ("$p", p.Id.ToString()));
        await Assert.ThrowsAsync<SecretUnavailableException>(() => s.Proxies.GetPasswordAsync(p.Id, T.Ct));
        Assert.Equal("pw2", await s.Proxies.GetPasswordAsync(other.Id, T.Ct));
        Assert.Equal(p.Id, Assert.Single(await s.UndecryptableRecords.ListAsync(ct: T.Ct)).RecordId);
    }
}
