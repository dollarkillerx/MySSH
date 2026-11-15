using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Porta.Pty;

namespace TermProto;

/// <summary>Spike host: a local PTY stands in for the SSH ShellStream so vim/htop/tmux/IME can be tried by hand.</summary>
public partial class MainWindow : Window
{
    IPtyConnection? _pty;
    readonly DispatcherTimer _status;
    long _bytesIn;

    public MainWindow()
    {
        InitializeComponent();
        Term.TitleChanged += t => Title = $"{t} — MySSH terminal spike";
        Term.Input += data => { try { _pty?.WriterStream.Write(data, 0, data.Length); _pty?.WriterStream.Flush(); } catch (IOException) { } };
        Term.TerminalResized += (c, r) => { try { _pty?.Resize(c, r); } catch { } };
        _status = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            Status.Text = $"{Term.Engine.Cols}×{Term.Engine.Rows} · render {Term.LastRenderTime.TotalMilliseconds:F1} ms · frames {Term.FramesRendered} · in {_bytesIn / 1024} KB · " +
                          $"alt={Term.Engine.IsAlternateBufferActive} mouse={Term.Engine.MouseTrackingMode} bracketed={Term.Engine.BracketedPasteMode}");
        _status.Start();
        Opened += async (_, _) => { Term.Focus(); await StartShellAsync(); };
        Closing += (_, _) => { try { _pty?.Kill(); } catch { } };
    }

    async Task StartShellAsync()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";
        var env = new Dictionary<string, string> { ["TERM"] = "xterm-256color", ["COLORTERM"] = "truecolor", ["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "en_US.UTF-8" };
        _pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "myssh-spike",
            App = shell,
            CommandLine = ["-l"],
            Cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Cols = Term.Engine.Cols,
            Rows = Term.Engine.Rows,
            Environment = env,
        }, default);
        _pty.ProcessExited += (_, e) => Dispatcher.UIThread.Post(() => Status.Text = $"shell exited ({e.ExitCode})");
        _ = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await _pty.ReaderStream.ReadAsync(buf)) > 0)
                {
                    _bytesIn += n;
                    Term.EnqueueOutput(buf.AsSpan(0, n));
                }
            }
            catch (IOException) { }
        });
    }
}
