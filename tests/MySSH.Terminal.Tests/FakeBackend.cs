using System.Text;
using MySSH.Core;

namespace MySSH.Terminal.Tests;

/// <summary>In-memory <see cref="ITerminalBackend"/>: tests push output and inspect what the view wrote back.</summary>
internal sealed class FakeBackend : ITerminalBackend
{
    private readonly List<byte> _written = [];

    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action<TerminalExit>? Exited;

    public List<(int Cols, int Rows)> Resizes { get; } = [];

    public bool Disposed { get; private set; }

    public string WrittenText
    {
        get
        {
            lock (_written)
            {
                return Encoding.UTF8.GetString([.. _written]);
            }
        }
    }

    public void ClearWritten()
    {
        lock (_written)
        {
            _written.Clear();
        }
    }

    public void Emit(string text) => Emit(Encoding.UTF8.GetBytes(text));

    public void Emit(byte[] data) => DataReceived?.Invoke(data);

    /// <summary>Emits from a thread-pool thread, like a real SSH reader.</summary>
    public void EmitFromBackground(byte[] data) => Task.Run(() => DataReceived?.Invoke(data)).Wait();

    public void Exit(TerminalExit exit) => Exited?.Invoke(exit);

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        lock (_written)
        {
            _written.AddRange(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows) => Resizes.Add((columns, rows));

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
