using MySSH.Core;
using Porta.Pty;

namespace MySSH.Terminal.Tests;

/// <summary>A local PTY as an <see cref="ITerminalBackend"/> (tests only): real programs drive the view end to end.</summary>
internal sealed class PtyBackend : ITerminalBackend
{
    private readonly IPtyConnection _pty;
    private readonly MemoryStream? _recording;
    private readonly CancellationTokenSource _cts = new();

    private PtyBackend(IPtyConnection pty, bool record)
    {
        _pty = pty;
        _recording = record ? new MemoryStream() : null;
        _pty.ProcessExited += (_, e) => Exited?.Invoke(new TerminalExit(e.ExitCode, false));
        _ = Task.Run(ReadLoop);
    }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<TerminalExit>? Exited;

    public bool HasExited => _pty.HasExited;

    public static bool CanRun(string app) => !OperatingSystem.IsWindows() && File.Exists(app);

    public static PtyBackend Spawn(string app, string[] args, int cols, int rows, string cwd, bool record = false, IDictionary<string, string>? env = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["LANG"] = "en_US.UTF-8",
            ["LC_ALL"] = "en_US.UTF-8",
            ["LESSCHARSET"] = "utf-8",
        };
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            environment[k] = v;
        }

        var pty = PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "test",
            App = app,
            CommandLine = args,
            Cols = cols,
            Rows = rows,
            Cwd = cwd,
            Environment = environment,
        }, CancellationToken.None).GetAwaiter().GetResult();
        return new PtyBackend(pty, record);
    }

    /// <summary>Everything the program wrote so far (when recording).</summary>
    public byte[] Recording()
    {
        lock (_recording!)
        {
            return _recording.ToArray();
        }
    }

    public DateTime LastOutput { get; private set; } = DateTime.UtcNow;

    private async Task ReadLoop()
    {
        var buf = new byte[65536];
        try
        {
            int n;
            while ((n = await _pty.ReaderStream.ReadAsync(buf, _cts.Token)) > 0)
            {
                if (_recording is not null)
                {
                    lock (_recording)
                    {
                        _recording.Write(buf, 0, n);
                    }
                }

                LastOutput = DateTime.UtcNow;
                DataReceived?.Invoke(buf.AsMemory(0, n));
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested || _pty.HasExited)
        {
        }
        catch (IOException)
        {
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        _pty.WriterStream.Write(data.Span);
        _pty.WriterStream.Flush();
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows) => _pty.Resize(columns, rows);

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (!_pty.HasExited)
            {
                _pty.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
