using Microsoft.Extensions.Logging.Abstractions;

namespace MySSH.Mcp;

/// <summary>
/// Forwards the in-process host's logs to the caller's <see cref="ILoggerFactory"/> without letting the host own (or
/// dispose) it, and drops the SDK's per-cancellation warnings (spike G11: every client cancellation is logged as a warning
/// with a full TaskCanceledException stack); those are logged at Debug instead.
/// </summary>
internal sealed class ForwardingLoggerProvider(ILoggerFactory target) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CancellationNoiseFilter(target.CreateLogger(categoryName));

    public void Dispose() { } // the caller owns the factory
}

/// <summary>Logger factory handed to the SDK for local sessions (same filtering as the HTTP host).</summary>
internal sealed class FilteringLoggerFactory(ILoggerFactory? target) : ILoggerFactory
{
    private readonly ILoggerFactory _target = target ?? NullLoggerFactory.Instance;

    public ILogger CreateLogger(string categoryName) => new CancellationNoiseFilter(_target.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) { } // never mutate the caller's factory

    public void Dispose() { }
}

internal sealed class CancellationNoiseFilter(ILogger inner) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (exception is OperationCanceledException && logLevel is LogLevel.Warning or LogLevel.Error)
        {
            if (inner.IsEnabled(LogLevel.Debug))
                inner.Log(LogLevel.Debug, eventId, state, null, formatter);
            return;
        }
        inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
