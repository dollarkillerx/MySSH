using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Porta.Pty;
using TermView = TermControl.TerminalControl;

/// <summary>Headless end-to-end: real PTY → TerminalControl, with engine replies (DA/DSR) written back, driving vim.</summary>
static class PtySmoke
{
    public static string Run(string outDir)
    {
        var log = new StringBuilder();
        var term = new TermView { TerminalFontSize = 14 };
        var win = new Window { Width = 900, Height = 420, Content = term };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var file = Path.Combine(outDir, "sample.conf");
        File.WriteAllText(file, "server {\n    listen 443 ssl;  # 中文注释 日本語\n}\n");
        var pty = PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "smoke", App = "/usr/bin/vim", CommandLine = ["-u", "NONE", "-N", "+set number", file],
            Cols = term.Engine.Cols, Rows = term.Engine.Rows, Cwd = outDir,
            Environment = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["LANG"] = "en_US.UTF-8" },
        }, default).GetAwaiter().GetResult();
        term.Input += d => { pty.WriterStream.Write(d); pty.WriterStream.Flush(); };
        term.TerminalResized += (c, r) => pty.Resize(c, r);
        _ = Task.Run(async () =>
        {
            var buf = new byte[65536]; int n;
            try { while ((n = await pty.ReaderStream.ReadAsync(buf)) > 0) term.EnqueueOutput(buf.AsSpan(0, n)); } catch { }
        });

        bool Wait(Func<bool> cond, int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Pump(term); if (cond()) return true; Thread.Sleep(20); }
            return false;
        }

        bool vimUp = Wait(() => term.Engine.IsAlternateBufferActive && term.GetVisibleText(50).Contains("listen 443"), 5000);
        log.AppendLine($"vim drew file in alt screen: {vimUp}");
        log.AppendLine($"  CJK line: '{term.GetVisibleText(50).Split('\n').FirstOrDefault(l => l.Contains("listen")) ?? "?"}'");
        win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "06-vim-real.png"));

        // resize while vim is running → SIGWINCH → vim redraws to the new size
        win.Width = 700; win.Height = 300;
        Dispatcher.UIThread.RunJobs();
        bool resized = Wait(() => term.GetVisibleText(50).Contains("listen 443"), 2000);
        log.AppendLine($"after resize to {term.Engine.Cols}x{term.Engine.Rows}: vim redrawn={resized}");

        // type through the control's input path: insert text incl. CJK, then save & quit
        void Send(string s) { var b = Encoding.UTF8.GetBytes(s); pty.WriterStream.Write(b); pty.WriterStream.Flush(); }
        Send("ggA 你好\u001b:wq\r");
        bool exited = Wait(() => pty.HasExited || !term.Engine.IsAlternateBufferActive, 5000);
        Thread.Sleep(200);
        log.AppendLine($"vim exited & alt screen left: {exited}; file now: {File.ReadAllText(file).Split('\n')[0]}");
        try { pty.Kill(); } catch { }
        return log.ToString();
    }

    static void Pump(TermView t)
    {
        // the control's DispatcherTimer does not tick under RunJobs(); drain like the timer would
        var m = typeof(TermView).GetMethod("Pump", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        m.Invoke(t, null);
    }
}
