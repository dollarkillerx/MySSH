using System.Globalization;
using System.Text;
using Avalonia.Headless.XUnit;

namespace MySSH.Terminal.Tests;

/// <summary>
/// Records the replay fixtures by running real programs in a local PTY attached to a <see cref="TerminalView"/>
/// (so the programs see the engine's DA/DSR answers). Opt-in: <c>MYSSH_RECORD_FIXTURES=1 dotnet test --filter FixtureRecorder</c>.
/// Content is synthetic (temp files, process lists filtered out) so fixtures carry no machine data.
/// </summary>
public class FixtureRecorder
{
    private static void RequireOptIn()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MYSSH_RECORD_FIXTURES") == "1", "set MYSSH_RECORD_FIXTURES=1 to re-record fixtures");
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "myssh-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Save(string name, PtyBackend pty, int cols, int rows)
    {
        var dir = Harness.FixtureDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".bin"), pty.Recording());
        File.WriteAllText(Path.Combine(dir, name + ".meta"), string.Create(CultureInfo.InvariantCulture, $"cols={cols}\nrows={rows}\n"));
    }

    private static void Quiet(Harness h, PtyBackend pty, int ms = 300) =>
        h.WaitFor(() => (DateTime.UtcNow - pty.LastOutput).TotalMilliseconds > ms, 5000);

    private static void Type(PtyBackend pty, string s) => pty.WriteAsync(Encoding.UTF8.GetBytes(s)).AsTask().Wait();

    internal const string VimSample = "server {\n    listen 443 ssl;  # 中文注释 日本語\n    server_name example.test;\n}\n";

    [AvaloniaFact]
    public async Task Record_vim()
    {
        RequireOptIn();
        const int cols = 80, rows = 20;
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "sample.conf"), VimSample);
        var h = Harness.ForGrid(cols, rows);
        var pty = PtyBackend.Spawn("/usr/bin/vim", ["-u", "NONE", "-N", "-i", "NONE", "+set number ttimeout ttimeoutlen=20", "sample.conf"], cols, rows, dir, record: true);
        h.View.Attach(pty);
        Assert.True(h.WaitFor(() => h.Screen().Contains("listen 443", StringComparison.Ordinal)));
        Type(pty, "2GA 你好\u001b");
        Assert.True(h.WaitFor(() => h.Screen().Contains("日本語 你好", StringComparison.Ordinal) && !h.Screen().Contains("-- INSERT --", StringComparison.Ordinal)));
        Quiet(h, pty);
        Save("vim", pty, cols, rows);
        await pty.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Record_less()
    {
        RequireOptIn();
        const int cols = 90, rows = 24;
        var dir = TempDir();
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo" };
        File.WriteAllLines(Path.Combine(dir, "app.log"), Enumerable.Range(1, 200).Select(i => $"line {i}: 中文日志 日本語ログ 第{i}行 {words[i % words.Length]}"));
        var h = Harness.ForGrid(cols, rows);
        var pty = PtyBackend.Spawn("/usr/bin/less", ["-R", "app.log"], cols, rows, dir, record: true);
        h.View.Attach(pty);
        Assert.True(h.WaitFor(() => h.Screen().Contains("line 1:", StringComparison.Ordinal)));
        Type(pty, " ");
        Assert.True(h.WaitFor(() => h.Screen().Contains("line 30:", StringComparison.Ordinal)));
        Type(pty, "/第100行\r");
        Assert.True(h.WaitFor(() => h.Screen().StartsWith("line 100:", StringComparison.Ordinal)));
        Quiet(h, pty);
        Save("less", pty, cols, rows);
        await pty.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Record_htop()
    {
        RequireOptIn();
        Assert.SkipUnless(File.Exists("/opt/homebrew/bin/htop"), "htop not installed");
        const int cols = 100, rows = 30;
        var h = Harness.ForGrid(cols, rows);
        // only PID 1 (launchd/init): meters, column titles and one system process; no user processes in the fixture
        var pty = PtyBackend.Spawn("/opt/homebrew/bin/htop", ["-p", "1", "--no-mouse"], cols, rows, TempDir(), record: true);
        h.View.Attach(pty);
        Assert.True(h.WaitFor(() => h.Screen().Contains("PID", StringComparison.Ordinal)));
        Quiet(h, pty, 500);
        Save("htop", pty, cols, rows);
        await pty.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task Record_top()
    {
        RequireOptIn();
        const int cols = 100, rows = 20;
        var h = Harness.ForGrid(cols, rows);
        // -n 0: summary only, no process list
        var pty = PtyBackend.Spawn("/usr/bin/top", ["-n", "0", "-s", "1"], cols, rows, TempDir(), record: true);
        h.View.Attach(pty);
        Assert.True(h.WaitFor(() => h.Screen().Contains("PhysMem", StringComparison.Ordinal)));
        Quiet(h, pty, 400);
        Save("top", pty, cols, rows);
        await pty.DisposeAsync();
    }
}
