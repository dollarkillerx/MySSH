using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using MySSH.Core;

namespace MySSH.Terminal.Tests;

/// <summary>Real program, real PTY, the view's own input path (keys, text, IME commit, resize → SIGWINCH).</summary>
public class PtyEndToEndTests
{
    [AvaloniaFact]
    public async Task Vim_edit_resize_and_save_through_a_real_pty()
    {
        Assert.SkipUnless(PtyBackend.CanRun("/usr/bin/vim"), "needs a POSIX PTY and /usr/bin/vim");
        var dir = Path.Combine(Path.GetTempPath(), "myssh-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "sample.conf");
        await File.WriteAllTextAsync(file, FixtureRecorder.VimSample);

        var h = Harness.ForGrid(80, 20);
        var pty = PtyBackend.Spawn("/usr/bin/vim", ["-u", "NONE", "-N", "-i", "NONE", "+set number ttimeout ttimeoutlen=20", "sample.conf"], 80, 20, dir);
        TerminalExit? ended = null;
        h.View.SessionEnded += (_, e) => ended = e;
        h.View.Attach(pty);
        try
        {
            Assert.True(h.WaitFor(() => h.View.Engine.IsAlternateBufferActive && h.Screen().Contains("listen 443", StringComparison.Ordinal)), h.Screen());

            // window shrinks → backend.Resize → SIGWINCH → vim redraws for the new grid
            h.Window.Width = 60 * h.View.CellSize.Width;
            h.Window.Height = 12 * h.View.CellSize.Height;
            Assert.True(h.WaitFor(() => h.View.Columns == 60 && h.Screen().Contains("listen 443", StringComparison.Ordinal) && h.Screen().Split('\n').Length <= 12));

            h.Window.KeyTextInput("2GA ");
            h.View.ImeClient.SetPreeditText("ni hao", 6); // composing: nothing is sent
            h.Pump();
            h.View.ImeClient.SetPreeditText(null);
            h.Window.KeyTextInput("你好");               // commit
            Assert.True(h.WaitFor(() => h.Screen().Contains("日本語 你好", StringComparison.Ordinal)), h.Screen());
            h.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            h.Window.KeyTextInput(":wq");
            h.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Assert.True(h.WaitFor(() => ended is not null, 5000), $"vim did not exit: hasExited={pty.HasExited} file={File.ReadAllText(file).Replace("\n", "|")} screen=\n{h.Screen()}");
            Assert.False(h.View.Engine.IsAlternateBufferActive);
            Assert.Equal("    listen 443 ssl;  # 中文注释 日本語 你好", (await File.ReadAllLinesAsync(file))[1]);
            Assert.Equal(0, ended!.ExitCode);
        }
        finally
        {
            await pty.DisposeAsync();
        }
    }
}
