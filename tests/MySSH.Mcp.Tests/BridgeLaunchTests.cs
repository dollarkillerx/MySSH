using System.Diagnostics;

namespace MySSH.Mcp.Tests;

/// <summary>myssh-mcp starts MySSH in the tray when it is not running (MYSSH_APP points at a fake app here).</summary>
public sealed class BridgeLaunchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"launched","version":"1"}}}""";

    /// <summary>A fake "MySSH" that only records its arguments; the test then plays the app and starts the server.</summary>
    private static async Task<(string App, string Marker)> FakeAppAsync(string dir)
    {
        var marker = Path.Combine(dir, "launched.txt");
        var app = Path.Combine(dir, "MySSH");
        await File.WriteAllTextAsync(app, "#!/bin/sh\necho \"$@\" > '" + marker + "'\n", Ct);
        File.SetUnixFileMode(app, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (app, marker);
    }

    private static async Task<(int Code, string Stderr)> RunToExit(Process p)
    {
        try
        {
            var stderr = p.StandardError.ReadToEndAsync(Ct);
            await p.WaitForExitAsync(Ct).WaitAsync(Wait.Timeout, Ct);
            return (p.ExitCode, await stderr);
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.Dispose();
        }
    }

    [Fact]
    public async Task Bridge_starts_MySSH_in_the_tray_and_connects_once_the_socket_appears()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket test");
        var dir = TestHost.NewTempDir();
        try
        {
            var (app, marker) = await FakeAppAsync(dir);
            var p = Bridge.Start(new Dictionary<string, string> { ["MYSSH_APP"] = app }, "--socket", TestHost.SocketAddress(dir));
            await Wait.Until(() => File.Exists(marker) && File.ReadAllText(marker).Contains("--tray", StringComparison.Ordinal));
            await using var t = await TestHost.StartAsync(http: false, dir: dir);
            await p.StandardInput.WriteLineAsync(Initialize);
            await p.StandardInput.FlushAsync(Ct);
            var line = await p.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(Wait.Timeout, Ct);
            Assert.Contains("\"name\":\"myssh\"", line, StringComparison.Ordinal);
            p.StandardInput.Close();
            var (code, stderr) = await RunToExit(p);
            Assert.Equal(0, code);
            Assert.Contains("started", stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task No_launch_flag_starts_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix domain socket test");
        var dir = TestHost.NewTempDir();
        try
        {
            var (app, marker) = await FakeAppAsync(dir);
            var (code, _) = await RunToExit(Bridge.Start(new Dictionary<string, string> { ["MYSSH_APP"] = app },
                "--socket", TestHost.SocketAddress(dir), "--no-launch"));
            Assert.Equal(3, code);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
