using System.Diagnostics;
using System.Text;
using MySSH.Core;
using MySSH.Ssh.Tests;

namespace MySSH.Ssh.IntegrationTests;

/// <summary>In-memory hosts/identities/known-hosts wired to the sshd environment.</summary>
sealed class Lab : IAsyncDisposable
{
    readonly SshdEnvironment _env;
    readonly List<SshConnector> _connectors = [];

    public Lab(SshdEnvironment env, HostKeyDecision decision = HostKeyDecision.AcceptAndSave, params string?[] passphraseAnswers)
    {
        _env = env;
        Verifier = new ScriptedVerifier(decision);
        Prompt = new ScriptedPrompt(passphraseAnswers);
        Plain = Ids.Add(new Identity { Label = "plain", Username = env.User, Kind = IdentityKind.PrivateKey }, new IdentitySecrets(null, env.KeyPlain, ""));
        Encrypted = Ids.Add(new Identity { Label = "encrypted", Username = env.User, Kind = IdentityKind.PrivateKey, HasPassphrase = true },
            new IdentitySecrets(null, env.KeyEncrypted, null));
    }

    public MemHosts Hosts { get; } = new();
    public MemIdentities Ids { get; } = new();
    public MemProxies Proxies { get; } = new();
    public MemKnownHosts Known { get; } = new();
    public ScriptedVerifier Verifier { get; set; }
    public ScriptedPrompt Prompt { get; set; }
    public Identity Plain { get; }
    public Identity Encrypted { get; }

    public Host AddHost(string instance, Identity? identity = null, IReadOnlyList<Host>? jumps = null, ProxyProfile? proxy = null, Guid? aiIdentity = null)
        => Hosts.Add(new Host
        {
            Label = instance,
            Hostname = "127.0.0.1",
            Port = _env[instance].Port,
            IdentityId = (identity ?? Plain).Id,
            JumpChain = jumps?.Select(j => j.Id).ToArray() ?? [],
            ProxyId = proxy?.Id,
            AiIdentityId = aiIdentity,
        });

    public SshConnector Connector(SshConnectorOptions? options = null)
    {
        var c = new SshConnector(Hosts, Ids, Proxies, Known, Verifier, Prompt, options ?? new SshConnectorOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            HeartbeatInterval = TimeSpan.Zero,
        });
        _connectors.Add(c);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _connectors) await c.DisposeAsync();
    }
}

static class TestUtil
{
    public static async Task<string> WaitForOutputAsync(StringBuilder sb, Func<string, bool> until, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (sb) if (until(sb.ToString())) return sb.ToString();
            await Task.Delay(30);
        }
        lock (sb) throw new TimeoutException("output so far:\n" + sb);
    }

    public static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 10_000, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(what ?? "condition not met");
            await Task.Delay(30);
        }
    }

    public static void WriteRandom(string path, int bytes, int seed = 1)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }
}
