using MySSH.Core;
using Renci.SshNet.Common;

namespace MySSH.Ssh.Tests;

public sealed class CredentialTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly Identity Id = new() { Label = "key", Username = "u", Kind = IdentityKind.PrivateKey, HasPassphrase = true };

    /// <summary>Fake key loader: the key is encrypted with "right".</summary>
    sealed class FakeLoader
    {
        public readonly List<string?> Attempts = [];
        public string Load(string key, string? pass)
        {
            Attempts.Add(pass);
            if (key == "corrupt") throw new SshException("Invalid private key file.");
            if (key == "plain") return "loaded";
            if (pass is null) throw new SshPassPhraseNullOrEmptyException("Private key is encrypted but passphrase is empty.");
            if (pass != "right") throw new SshException("The random check bytes of the OpenSSH key do not match (wrong passphrase?).");
            return "loaded";
        }
    }

    static Task<string> Load(FakeLoader l, string key, string? stored, ConnectionOrigin origin, ICredentialPrompt prompt, int max = 3)
        => Credentials.LoadKeyAsync(Id, key, stored, origin, prompt, l.Load, max, 0, "host", Ct);

    [Fact]
    public async Task Stored_passphrase_needs_no_prompt_and_empty_means_none()
    {
        var l = new FakeLoader();
        var p = new ScriptedPrompt();
        Assert.Equal("loaded", await Load(l, "enc", "right", ConnectionOrigin.User, p));
        Assert.Equal("loaded", await Load(l, "plain", "", ConnectionOrigin.Ai, p));
        Assert.Equal(["right", null], l.Attempts);
        Assert.Empty(p.Errors);
    }

    [Fact]
    public async Task Missing_passphrase_prompts_and_wrong_answers_retry_with_an_error()
    {
        var l = new FakeLoader();
        var p = new ScriptedPrompt("bad", "", "right");
        Assert.Equal("loaded", await Load(l, "enc", null, ConnectionOrigin.User, p));
        Assert.Equal([null, Credentials.WrongPassphraseMessage, null], p.Errors);
        Assert.Equal([null, "bad", null, "right"], l.Attempts);
    }

    [Fact]
    public async Task Wrong_stored_passphrase_prompts_with_a_specific_message()
    {
        var l = new FakeLoader();
        var p = new ScriptedPrompt("right");
        Assert.Equal("loaded", await Load(l, "enc", "stale", ConnectionOrigin.User, p));
        Assert.Equal(["The saved passphrase is not correct."], p.Errors);
    }

    [Fact]
    public async Task Too_many_wrong_answers_give_wrong_passphrase()
    {
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => Load(new FakeLoader(), "enc", null, ConnectionOrigin.User, new ScriptedPrompt("a", "b", "c", "d"), max: 3));
        Assert.Equal(SshFailureKind.WrongPassphrase, ex.Kind);
    }

    [Fact]
    public async Task Cancel_strict_and_corrupt_keys()
    {
        var cancelled = await Assert.ThrowsAsync<SshConnectException>(() => Load(new FakeLoader(), "enc", null, ConnectionOrigin.User, new ScriptedPrompt((string?)null)));
        Assert.Equal(SshFailureKind.PassphraseCancelled, cancelled.Kind);

        var p = new ScriptedPrompt("right");
        var strict = await Assert.ThrowsAsync<SshConnectException>(() => Load(new FakeLoader(), "enc", null, ConnectionOrigin.Mcp, p));
        Assert.Equal(SshFailureKind.PassphraseRequired, strict.Kind);
        var strictWrong = await Assert.ThrowsAsync<SshConnectException>(() => Load(new FakeLoader(), "enc", "stale", ConnectionOrigin.Ai, p));
        Assert.Equal(SshFailureKind.WrongPassphrase, strictWrong.Kind);
        Assert.Empty(p.Errors);

        var corrupt = await Assert.ThrowsAsync<SshConnectException>(() => Load(new FakeLoader(), "corrupt", null, ConnectionOrigin.User, p));
        Assert.Equal(SshFailureKind.InvalidKey, corrupt.Kind);
    }

    const string EncryptedKey = """
        -----BEGIN OPENSSH PRIVATE KEY-----
        b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHIAAAAGYmNyeXB0AAAAGAAAABCIpbzO5I
        c5BhGaU9bXh4JtAAAAGAAAAAEAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIEdP1aScT0CLOxjN
        MfeuDAyDnrTcugCPaZ/CSnScu9LMAAAAkJDS29xF/G9/YdiHX1M/Owworp1IIlqoAE2lC4
        r3DGpIq/pgeqYKBEyaY98xE3DxHDrraTxhypCCNzJ5WKs71fPS9v0E6RqV2DG9m+YKMHdu
        Fts+jwhAx3bJ25glTAkexiaVOmnc39Kig4mRqXfE/62dhy0Lo/CozJOHDFyrRobg7CR2iK
        D94gjF+lduo0eWJg==
        -----END OPENSSH PRIVATE KEY-----
        """; // test-only key, passphrase "unit-pass"

    [Fact]
    public async Task Real_encrypted_openssh_key_goes_through_the_prompt()
    {
        var p = new ScriptedPrompt("nope", "unit-pass");
        using var key = await Credentials.LoadKeyAsync(Id, EncryptedKey, "", ConnectionOrigin.User, p, Credentials.LoadPrivateKey, 3, 0, "h", Ct);
        Assert.NotNull(key.Key);
        Assert.Equal([null, Credentials.WrongPassphraseMessage], p.Errors);
    }

    [Fact]
    public async Task Build_requires_identity_username_and_secrets()
    {
        var ids = new MemIdentities();
        var noUser = ids.Add(new Identity { Label = "nouser", Kind = IdentityKind.Password }, new IdentitySecrets("pw", null, null));
        var noSecret = ids.Add(new Identity { Label = "nosecret", Username = "u", Kind = IdentityKind.Password }, null);
        var pw = ids.Add(new Identity { Label = "pw", Username = "u", Kind = IdentityKind.Password }, new IdentitySecrets("pw", null, null));
        var host = new Host { Label = "h", Hostname = "h" };
        var plan = new ChainPlan([new PlannedHop(0, host, null, "u")], null, ConnectionOrigin.User);
        async Task<SshFailureKind> Fail(PlannedHop hop) => (await Assert.ThrowsAsync<SshConnectException>(() =>
            Credentials.BuildAsync(hop, plan, ids, new ScriptedPrompt(), 3, [], Ct))).Kind;
        Assert.Equal(SshFailureKind.InvalidConfiguration, await Fail(new PlannedHop(0, host, null, "u")));
        Assert.Equal(SshFailureKind.InvalidConfiguration, await Fail(new PlannedHop(0, host, noUser, null)));
        Assert.Equal(SshFailureKind.InvalidConfiguration, await Fail(new PlannedHop(0, host, noSecret, "u")));
        var owned = new List<IDisposable>();
        var methods = await Credentials.BuildAsync(new PlannedHop(0, host, pw, "u"), plan, ids, new ScriptedPrompt(), 3, owned, Ct);
        Assert.Equal(["password", "keyboard-interactive"], methods.Select(m => m.Name));
        foreach (var d in owned) d.Dispose();
    }
}

public sealed class PoolTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class FakeChain : IPooledChain
    {
        public bool Alive = true;
        public bool Transient;
        public int Disposed;
        public bool IsReusableFor(ConnectionOrigin origin) => Alive && (origin == ConnectionOrigin.User || !Transient);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposed); return ValueTask.CompletedTask; }
    }

    static readonly PoolKey Key = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task Refcount_last_release_disposes_once()
    {
        var pool = new ConnectionPool<FakeChain>();
        int created = 0;
        Task<FakeChain> Factory(CancellationToken _) { created++; return Task.FromResult(new FakeChain()); }
        var a = await pool.AcquireAsync(Key, ConnectionOrigin.User, Factory, Ct);
        var b = await pool.AcquireAsync(Key, ConnectionOrigin.User, Factory, Ct);
        Assert.Same(a.Chain, b.Chain);
        Assert.Equal(1, created);
        Assert.Equal(2, pool.RefCount(Key));
        var channel = b.AddRef();
        await a.DisposeAsync();
        await a.DisposeAsync(); // idempotent
        Assert.Equal(2, pool.RefCount(Key));
        await b.DisposeAsync();
        Assert.Equal(0, a.Chain.Disposed);
        await channel.DisposeAsync();
        Assert.Equal(1, a.Chain.Disposed);
        Assert.Equal(0, pool.EntryCount);
        Assert.Throws<ObjectDisposedException>(() => a.AddRef());
    }

    [Fact]
    public async Task Lost_chains_and_transient_trust_are_not_reused_by_strict_origins()
    {
        var pool = new ConnectionPool<FakeChain>();
        var a = await pool.AcquireAsync(Key, ConnectionOrigin.User, _ => Task.FromResult(new FakeChain { Transient = true }), Ct);
        var user = await pool.AcquireAsync(Key, ConnectionOrigin.User, _ => Task.FromResult(new FakeChain()), Ct);
        Assert.Same(a.Chain, user.Chain);
        var ai = await pool.AcquireAsync(Key, ConnectionOrigin.Ai, _ => Task.FromResult(new FakeChain()), Ct);
        Assert.NotSame(a.Chain, ai.Chain);
        Assert.Equal(2, pool.EntryCount);

        ai.Chain.Alive = false;
        var ai2 = await pool.AcquireAsync(Key, ConnectionOrigin.Mcp, _ => Task.FromResult(new FakeChain()), Ct);
        Assert.NotSame(ai.Chain, ai2.Chain);
        Assert.Equal(3, pool.EntryCount);
        var other = await pool.AcquireAsync(Key with { IdentityId = null }, ConnectionOrigin.User, _ => Task.FromResult(new FakeChain()), Ct);
        Assert.NotSame(a.Chain, other.Chain);
        await pool.DisposeAsync();
        Assert.Equal(1, a.Chain.Disposed);
        Assert.Equal(1, ai.Chain.Disposed);
        await a.DisposeAsync(); // after pool dispose: no double dispose
        Assert.Equal(1, a.Chain.Disposed);
    }

    [Fact]
    public async Task Concurrent_connects_for_one_key_share_one_chain_and_failures_are_not_pooled()
    {
        var pool = new ConnectionPool<FakeChain>();
        int created = 0;
        var gate = new TaskCompletionSource();
        async Task<FakeChain> Slow(CancellationToken _) { Interlocked.Increment(ref created); await gate.Task; return new FakeChain(); }
        var t1 = pool.AcquireAsync(Key, ConnectionOrigin.User, Slow, Ct);
        var t2 = pool.AcquireAsync(Key, ConnectionOrigin.User, Slow, Ct);
        gate.SetResult();
        var r = await Task.WhenAll(t1, t2);
        Assert.Same(r[0].Chain, r[1].Chain);
        Assert.Equal(1, created);

        var k2 = new PoolKey(Guid.NewGuid(), null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.AcquireAsync(k2, ConnectionOrigin.User, _ => throw new InvalidOperationException("boom"), Ct));
        Assert.Equal(0, pool.RefCount(k2));
        using var cts = new CancellationTokenSource();
        var blocker = new TaskCompletionSource();
        var hold = pool.AcquireAsync(k2, ConnectionOrigin.User, async _ => { await blocker.Task; return new FakeChain(); }, Ct);
        var waiting = pool.AcquireAsync(k2, ConnectionOrigin.User, _ => Task.FromResult(new FakeChain()), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        blocker.SetResult();
        await hold;
        Assert.Equal(1, pool.RefCount(k2));
    }
}
