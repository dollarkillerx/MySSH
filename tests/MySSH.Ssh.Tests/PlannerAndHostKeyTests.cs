using MySSH.Core;

namespace MySSH.Ssh.Tests;

public sealed class ChainPlannerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static Host H(string label, params Host[] jumps) => new()
    {
        Label = label, Hostname = label + ".example", JumpChain = [.. jumps.Select(j => j.Id)],
    };

    static async Task<string[]> PathOf(Host target, MemHosts hosts)
        => [.. (await ChainPlanner.ResolvePathAsync(target, (id, c) => hosts.GetAsync(id, c), Ct)).Path.Select(h => h.Label)];

    [Fact]
    public async Task Direct_host_is_a_single_hop()
    {
        var t = H("t");
        Assert.Equal(["t"], await PathOf(t, new MemHosts(t)));
    }

    [Fact]
    public async Task Jump_chain_is_in_connection_order_and_the_first_jump_is_expanded()
    {
        var j0inner = H("j0-inner");
        var j0 = H("j0", j0inner);
        var j2own = H("j2-own");
        var j1 = H("j1");
        var j2 = H("j2", j2own); // reached through j1: its own chain is not used
        var t = H("t", j0, j1, j2);
        Assert.Equal(["j0-inner", "j0", "j1", "j2", "t"], await PathOf(t, new MemHosts(j0inner, j0, j1, j2, j2own, t)));
    }

    [Fact]
    public async Task Proxy_applies_to_the_first_hop_only_and_target_proxy_wins()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var j0 = H("j0") with { ProxyId = p1 };
        var t = H("t", j0);
        var hosts = new MemHosts(j0, t);
        Assert.Equal(p1, (await ChainPlanner.ResolvePathAsync(t, (id, c) => hosts.GetAsync(id, c), Ct)).ProxyId);
        var t2 = t with { ProxyId = p2 };
        Assert.Equal(p2, (await ChainPlanner.ResolvePathAsync(t2, (id, c) => hosts.GetAsync(id, c), Ct)).ProxyId);

        var ids = new MemIdentities();
        var proxies = new MemProxies();
        var proxy = proxies.Add(new ProxyProfile { Id = p2, Label = "socks", Kind = ProxyKind.Socks5, Hostname = "p", Port = 1080 });
        hosts.Add(t2);
        var plan = await ChainPlanner.PlanAsync(t2.Id, ConnectionOrigin.User, hosts, ids, proxies, Ct);
        Assert.Equal(proxy, plan.Proxy);
        Assert.Equal(["j0", "t"], plan.Hops.Select(h => h.Host.Label));
        Assert.Equal([0, 1], plan.Hops.Select(h => h.Index));
    }

    [Fact]
    public async Task Cycles_and_duplicates_are_detected()
    {
        var a = H("a");
        var b = H("b", a);
        a = a with { JumpChain = [b.Id] };
        var hosts = new MemHosts(a, b);
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => PathOf(b, hosts));
        Assert.Equal(SshFailureKind.InvalidConfiguration, ex.Kind);
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);

        var self = H("self");
        self = self with { JumpChain = [self.Id] };
        ex = await Assert.ThrowsAsync<SshConnectException>(() => PathOf(self, new MemHosts(self)));
        Assert.Contains("itself", ex.Message, StringComparison.Ordinal);

        var j = H("j");
        var dup = H("dup", j, j);
        ex = await Assert.ThrowsAsync<SshConnectException>(() => PathOf(dup, new MemHosts(j, dup)));
        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);

        // indirect: t → j0, j0 → x, and x listed again later in t's chain
        var x = H("x");
        var j0 = H("j0", x);
        var t = H("t", j0, x);
        ex = await Assert.ThrowsAsync<SshConnectException>(() => PathOf(t, new MemHosts(x, j0, t)));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_hop_is_a_clear_error()
    {
        var t = new Host { Label = "t", Hostname = "t", JumpChain = [Guid.NewGuid()] };
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => PathOf(t, new MemHosts(t)));
        Assert.Contains("Jump host #1 of 't'", ex.Message, StringComparison.Ordinal);
        var missingHost = await Assert.ThrowsAsync<SshConnectException>(() =>
            ChainPlanner.PlanAsync(Guid.NewGuid(), ConnectionOrigin.User, new MemHosts(), new MemIdentities(), new MemProxies(), Ct));
        Assert.Equal(SshFailureKind.InvalidConfiguration, missingHost.Kind);
    }

    [Theory]
    [InlineData(ConnectionOrigin.User, "user-key")]
    [InlineData(ConnectionOrigin.Ai, "ai-key")]
    [InlineData(ConnectionOrigin.Mcp, "ai-key")]
    public async Task Ai_identity_is_used_on_the_target_for_ai_and_mcp_only(ConnectionOrigin origin, string expected)
    {
        var ids = new MemIdentities();
        var user = ids.Add(new Identity { Label = "user-key", Username = "alice" }, null);
        var ai = ids.Add(new Identity { Label = "ai-key", Username = "robot" }, null);
        var jumpId = ids.Add(new Identity { Label = "jump-key", Username = "jumper" }, null);
        var jump = new Host { Label = "jump", Hostname = "j", IdentityId = jumpId.Id, AiIdentityId = ai.Id };
        var t = new Host { Label = "t", Hostname = "t", IdentityId = user.Id, AiIdentityId = ai.Id, JumpChain = [jump.Id] };
        var plan = await ChainPlanner.PlanAsync(t.Id, origin, new MemHosts(jump, t), ids, new MemProxies(), Ct);
        Assert.Equal(expected, plan.Target.Identity!.Label);
        Assert.Equal("jump-key", plan.Hops[0].Identity!.Label);
        Assert.Equal(expected == "ai-key" ? "robot" : "alice", plan.Target.Username);
    }

    [Fact]
    public async Task Host_username_overrides_identity_username_and_missing_proxy_fails()
    {
        var ids = new MemIdentities();
        var id = ids.Add(new Identity { Label = "k", Username = "fromidentity" }, null);
        var t = new Host { Label = "t", Hostname = "t", Port = 2200, Username = "fromhost", IdentityId = id.Id };
        var plan = await ChainPlanner.PlanAsync(t.Id, ConnectionOrigin.User, new MemHosts(t), ids, new MemProxies(), Ct);
        Assert.Equal("fromhost", plan.Target.Username);
        Assert.Equal("[t]:2200", plan.Target.HostPort);

        var t2 = t with { Id = Guid.NewGuid(), ProxyId = Guid.NewGuid() };
        var ex = await Assert.ThrowsAsync<SshConnectException>(() =>
            ChainPlanner.PlanAsync(t2.Id, ConnectionOrigin.User, new MemHosts(t2), ids, new MemProxies(), Ct));
        Assert.Contains("Proxy", ex.Message, StringComparison.Ordinal);
    }
}

public sealed class HostKeyTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ssh-keygen -lf: 256 SHA256:MxUs8sJRW7jemDYPlHBRlcHBe1OfD0kURBs9lyrAaPs fixture (ED25519)
    const string FixturePub = "AAAAC3NzaC1lZDI1NTE5AAAAIFczVDXwyzBiBpaYA0QGdzD4JkDkTb7/DXksvoH7swLU";
    const string FixtureFp = "SHA256:MxUs8sJRW7jemDYPlHBRlcHBe1OfD0kURBs9lyrAaPs";
    static readonly byte[] Blob = Convert.FromBase64String(FixturePub);
    static readonly byte[] Other = [.. Blob[..^1], (byte)(Blob[^1] ^ 1)];

    static KnownHostEntry Entry(string type, byte[] blob, bool withKey = true) => new()
    {
        HostPort = "[h]:2222", KeyType = type, Sha256Fingerprint = HostKeyFingerprint.Sha256(blob),
        PublicKeyBase64 = withKey ? Convert.ToBase64String(blob) : null,
    };

    [Fact]
    public void Fingerprint_matches_ssh_keygen()
    {
        Assert.Equal(FixtureFp, HostKeyFingerprint.Sha256(Blob));
        Assert.DoesNotContain("=", HostKeyFingerprint.Sha256(Other), StringComparison.Ordinal);
    }

    [Fact]
    public void Known_host_port_format_omits_port_22()
    {
        Assert.Equal("example.com", KnownHostEntry.FormatHostPort("example.com", 22));
        Assert.Equal("[127.0.0.1]:2222", KnownHostEntry.FormatHostPort("127.0.0.1", 2222));
    }

    [Fact]
    public void Evaluate_store_hit_miss_and_changed()
    {
        Assert.Equal(HostKeyMatch.Unknown, HostKeyPolicy.Evaluate([], "ssh-ed25519", Blob).Match);
        Assert.Equal(HostKeyMatch.Match, HostKeyPolicy.Evaluate([Entry("ssh-ed25519", Blob)], "ssh-ed25519", Blob).Match);
        Assert.Equal(HostKeyMatch.Match, HostKeyPolicy.Evaluate([Entry("ssh-ed25519", Blob, withKey: false)], "ssh-ed25519", Blob).Match);
        var changed = HostKeyPolicy.Evaluate([Entry("ssh-ed25519", Blob)], "ssh-ed25519", Other);
        Assert.Equal(HostKeyMatch.Changed, changed.Match);
        Assert.Equal(FixtureFp, changed.PreviousFingerprint);
        // a known host suddenly presenting another key type is treated as changed
        var otherType = HostKeyPolicy.Evaluate([Entry("ssh-ed25519", Blob)], "rsa-sha2-512", Other);
        Assert.Equal(HostKeyMatch.Changed, otherType.Match);
        // several key types stored: the matching one wins
        Assert.Equal(HostKeyMatch.Match, HostKeyPolicy.Evaluate([Entry("rsa-sha2-512", Other), Entry("ssh-ed25519", Blob)], "ssh-ed25519", Blob).Match);
    }

    [Fact]
    public void Gate_compares_against_the_pinned_key_when_set()
    {
        var pinned = new ObservedHostKey("ssh-ed25519", Blob, new(HostKeyMatch.Match, null));
        var gate = new HostKeyGate([], pinned);
        Assert.True(gate.Check("ssh-ed25519", Blob));
        Assert.False(gate.Check("ssh-ed25519", Other));
        Assert.Equal(HostKeyMatch.Changed, gate.Observed!.Evaluation.Match);
        var tofu = new HostKeyGate([], null);
        Assert.False(tofu.Check("ssh-ed25519", Blob));
        Assert.Equal(HostKeyMatch.Unknown, tofu.Observed!.Evaluation.Match);
    }

    static ObservedHostKey Observed(HostKeyMatch m, string? prev = null) => new("ssh-ed25519", Blob, new(m, prev));

    [Theory]
    [InlineData(ConnectionOrigin.Ai, false, SshFailureKind.HostKeyUnknown)]
    [InlineData(ConnectionOrigin.Mcp, false, SshFailureKind.HostKeyUnknown)]
    [InlineData(ConnectionOrigin.Ai, true, SshFailureKind.HostKeyChanged)]
    [InlineData(ConnectionOrigin.Mcp, true, SshFailureKind.HostKeyChanged)]
    public async Task Strict_origins_fail_without_asking(ConnectionOrigin origin, bool changed, SshFailureKind expected)
    {
        var match = changed ? HostKeyMatch.Changed : HostKeyMatch.Unknown;
        var verifier = new ScriptedVerifier(HostKeyDecision.AcceptAndSave);
        var store = new MemKnownHosts();
        var ex = await Assert.ThrowsAsync<SshConnectException>(() =>
            HostKeyPolicy.ResolveAsync(Observed(match, "SHA256:old"), "[h]:2222", "h", 1, 2, origin, verifier, store, Ct));
        Assert.Equal(expected, ex.Kind);
        Assert.Equal(1, ex.HopIndex);
        Assert.Empty(verifier.Checks);
        Assert.Equal(0, store.Upserts);
    }

    [Fact]
    public async Task User_accept_and_save_persists_with_hop_info()
    {
        var verifier = new ScriptedVerifier(HostKeyDecision.AcceptAndSave);
        var store = new MemKnownHosts();
        var once = await HostKeyPolicy.ResolveAsync(Observed(HostKeyMatch.Changed, "SHA256:old"), "[h]:2222", "label", 1, 3, ConnectionOrigin.User, verifier, store, Ct);
        Assert.False(once);
        var check = Assert.Single(verifier.Checks);
        Assert.Equal(new HostKeyCheck("[h]:2222", "ssh-ed25519", FixtureFp, FixturePub, HostKeyStatus.Changed, "SHA256:old", "label", 1, 3), check);
        var saved = Assert.Single(await store.FindAsync("[h]:2222", Ct));
        Assert.Equal((FixtureFp, FixturePub, "ssh-ed25519"), (saved.Sha256Fingerprint, saved.PublicKeyBase64, saved.KeyType));
    }

    [Fact]
    public async Task User_accept_once_does_not_persist_and_reject_fails()
    {
        var store = new MemKnownHosts();
        Assert.True(await HostKeyPolicy.ResolveAsync(Observed(HostKeyMatch.Unknown), "h", "h", 0, 1, ConnectionOrigin.User,
            new ScriptedVerifier(HostKeyDecision.AcceptOnce), store, Ct));
        Assert.Equal(0, store.Upserts);
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => HostKeyPolicy.ResolveAsync(Observed(HostKeyMatch.Unknown), "h", "h", 0, 1,
            ConnectionOrigin.User, new ScriptedVerifier(HostKeyDecision.Reject), store, Ct));
        Assert.Equal(SshFailureKind.HostKeyRejected, ex.Kind);
        // a key that already matches never reaches the verifier
        var v = new ScriptedVerifier(HostKeyDecision.Reject);
        Assert.False(await HostKeyPolicy.ResolveAsync(Observed(HostKeyMatch.Match), "h", "h", 0, 1, ConnectionOrigin.Ai, v, store, Ct));
        Assert.Empty(v.Checks);
    }
}
