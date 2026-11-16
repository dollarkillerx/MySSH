using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Renci.SshNet;

namespace MySSH.Ssh;

/// <summary>
/// The few SSH.NET internals MySSH needs, reached through <see cref="UnsafeAccessorAttribute"/> (+ .NET 10
/// <see cref="UnsafeAccessorTypeAttribute"/> for internal types). Unlike reflection this is trimming/NativeAOT safe: the
/// accessors are bound at compile time (ILC) or first call (JIT); a member that no longer exists throws
/// <see cref="MissingMemberException"/> at the call site, which every wrapper turns into a "not available" result.
/// Verified against SSH.NET 2026.0.1 (pinned in Directory.Packages.props).
/// </summary>
internal static class SshNetInternals
{
    const string Asm = ", Renci.SshNet";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Session")]
    [return: UnsafeAccessorType("Renci.SshNet.ISession" + Asm)]
    static extern object? GetSessionRaw(BaseClient client);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Renci.SshNet.ISession.CreateChannelDirectTcpip")]
    [return: UnsafeAccessorType("Renci.SshNet.Channels.IChannelDirectTcpip" + Asm)]
    static extern object CreateChannelDirectTcpipRaw(Session session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Open")]
    static extern void DirectTcpipOpenRaw([UnsafeAccessorType("Renci.SshNet.Channels.ChannelDirectTcpip" + Asm)] object channel,
        string remoteHost, uint port, IForwardedPort forwardedPort, Socket socket);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Bind")]
    static extern void DirectTcpipBindRaw([UnsafeAccessorType("Renci.SshNet.Channels.ChannelDirectTcpip" + Asm)] object channel);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern Renci.SshNet.Messages.Connection.GlobalRequestMessage NewGlobalRequestRaw(byte[] requestName, bool wantReply);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendMessage")]
    static extern void SendMessageRaw(Session session, Renci.SshNet.Messages.Message message);

    static bool IsMissing(Exception ex) => ex is MissingMemberException or TypeLoadException or InvalidCastException or BadImageFormatException;

    /// <summary>The public <see cref="Session"/> behind a client (null when not connected or the accessor is unavailable).</summary>
    public static Session? TryGetSession(BaseClient client)
    {
        try { return GetSessionRaw(client) as Session; }
        catch (Exception ex) when (IsMissing(ex)) { return null; }
    }

    public static object CreateDirectTcpip(Session session) => CreateChannelDirectTcpipRaw(session);
    public static void DirectTcpipOpen(object channel, string host, uint port, IForwardedPort owner, Socket socket) => DirectTcpipOpenRaw(channel, host, port, owner, socket);
    public static void DirectTcpipBind(object channel) => DirectTcpipBindRaw(channel);

    /// <summary>Sends <c>keepalive@openssh.com</c> with want-reply (what OpenSSH's ServerAliveInterval does).</summary>
    public static void SendKeepAliveRequest(Session session)
        => SendMessageRaw(session, NewGlobalRequestRaw("keepalive@openssh.com"u8.ToArray(), true));

    public static bool IsGlobalRequestSupported => s_globalRequestSupported.Value;

    static readonly Lazy<bool> s_globalRequestSupported = new(() =>
        ProbeSession() && Probe(() => SendMessageRaw(null!, null!)) && Probe(() => NewGlobalRequestRaw([], true)));

    static readonly Lazy<bool> s_bridgeSupported = new(ProbeBridge);

    /// <summary>
    /// Startup self-check for <see cref="SingleAcceptBridgeTransport"/>: every accessor must bind. Calling an instance accessor
    /// with a null receiver throws <see cref="NullReferenceException"/> when the member exists (no side effects) and
    /// <see cref="MissingMemberException"/> when it does not — identical under JIT and NativeAOT.
    /// </summary>
    public static bool IsBridgeSupported => s_bridgeSupported.Value;

    /// <summary>Exit status only needs the public <see cref="Session"/> events (channel numbers come from CHANNEL_OPEN_CONFIRMATION).</summary>
    public static bool IsExitStatusSupported => s_exitStatusSupported.Value;

    static readonly Lazy<bool> s_exitStatusSupported = new(ProbeSession);

    static bool ProbeSession()
    {
        try
        {
            using var c = new SshClient("127.0.0.1", "probe", "probe");
            _ = GetSessionRaw(c);
            return true;
        }
        catch (Exception ex) when (IsMissing(ex)) { return false; }
    }

    static bool ProbeBridge() =>
        ProbeSession()
        && Probe(() => CreateChannelDirectTcpipRaw(null!))
        && Probe(() => DirectTcpipOpenRaw(null!, "", 0, null!, null!))
        && Probe(() => DirectTcpipBindRaw(null!));

    static bool Probe(Action call)
    {
        try { call(); return true; }
        catch (NullReferenceException) { return true; }
        catch (Exception ex) when (IsMissing(ex)) { return false; }
#pragma warning disable CA1031 // self-check: any other failure means "do not use the internals"
        catch (Exception) { return false; }
#pragma warning restore CA1031
    }
}
