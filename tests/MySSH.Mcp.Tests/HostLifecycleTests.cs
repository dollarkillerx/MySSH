using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MySSH.Mcp.Tests;

public sealed class HostLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Both_transports_disabled_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => McpServerHost.StartAsync(
            new McpHostOptions { EnableHttp = false, EnableLocalSocket = false }, FakeCatalog.Instance, new FakeInvoker(), ct: Ct));
    }

    [Fact]
    public async Task Port_in_use_fails_and_releases_the_local_socket()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var dir = TestHost.NewTempDir();
        try
        {
            var socket = TestHost.SocketAddress(dir);
            await Assert.ThrowsAnyAsync<IOException>(() => McpServerHost.StartAsync(
                new McpHostOptions { Port = port, LocalSocketPath = socket }, FakeCatalog.Instance, new FakeInvoker(), ct: Ct));
            if (!OperatingSystem.IsWindows()) Assert.False(File.Exists(socket));

            // the address is free again
            await using var host = await McpServerHost.StartAsync(
                new McpHostOptions { EnableHttp = false, LocalSocketPath = socket }, FakeCatalog.Instance, new FakeInvoker(), ct: Ct);
            Assert.Equal(socket, host.LocalSocketPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellations_are_not_logged_as_warnings()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "uses the Unix socket");
        var sink = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(sink));
        var invoker = new FakeInvoker();
        var dir = TestHost.NewTempDir();
        var host = await McpServerHost.StartAsync(new McpHostOptions { LocalSocketPath = TestHost.SocketAddress(dir) },
            FakeCatalog.Instance, invoker, factory, Ct);
        try
        {
            var transport = new ModelContextProtocol.Client.HttpClientTransport(new ModelContextProtocol.Client.HttpClientTransportOptions
            {
                Endpoint = host.HttpEndpoint!,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + host.Token },
            });
            await using (var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport, TestHost.ClientOptions(TestHost.Legacy), cancellationToken: Ct))
            {
                foreach (var _ in Enumerable.Range(0, 3))
                {
                    var marker = Guid.NewGuid().ToString("N");
                    using var cts = new CancellationTokenSource();
                    var call = client.CallToolAsync("slow", new Dictionary<string, object?> { ["marker"] = marker }, cancellationToken: cts.Token).AsTask();
                    await invoker.StartedFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
                    await cts.CancelAsync();
                    await Assert.ThrowsAnyAsync<Exception>(() => call);
                    await invoker.CancelledFor(marker).Task.WaitAsync(Wait.Timeout, Ct);
                }
            }
            await Task.Delay(200, Ct);
        }
        finally
        {
            await host.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
        Assert.Contains(sink.Entries, e => e.Category.StartsWith("MySSH.Mcp", StringComparison.Ordinal)); // logging reaches the caller
        Assert.DoesNotContain(sink.Entries, e => e.Level >= LogLevel.Warning && e.Exception is OperationCanceledException);
    }

    [Fact]
    public void Http_client_entries_are_capped()
    {
        var tracker = new ClientTracker(TimeProvider.System);
        for (var i = 0; i < ClientTracker.MaxHttpEntries + 10; i++)
            tracker.Touch(tracker.Http($"c{i}", null), $"c{i}", null);
        var all = tracker.Snapshot();
        Assert.Equal(ClientTracker.MaxHttpEntries, all.Count);
        Assert.Contains(all, c => c.Name == $"c{ClientTracker.MaxHttpEntries + 9}");
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new L(this, categoryName);
        public void Dispose() { }

        private sealed class L(ListLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }
}
