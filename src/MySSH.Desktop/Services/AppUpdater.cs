using Velopack;
using Velopack.Sources;

namespace MySSH.Desktop.Services;

/// <summary>
/// Install / auto-update (design §12, P5). Only a packaged build (tools/release) is "installed"; `dotnet run`, tests and a
/// hand-built .app report <see cref="IsInstalled"/> = false and never update.
/// </summary>
public interface IAppUpdater
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    /// <summary>Downloaded by an earlier run and not installed yet (installs on the next restart through Velopack).</summary>
    string? PendingVersion { get; }
    /// <summary>The newer version on the release feed, or null when this one is current.</summary>
    Task<string?> CheckAsync(CancellationToken ct);
    /// <summary>Downloads what <see cref="CheckAsync"/> found. <paramref name="progress"/> (0–100) runs on a worker thread.</summary>
    Task DownloadAsync(Action<int> progress, CancellationToken ct);
    /// <summary>Starts Velopack's updater, which waits for this process to exit, installs the update and starts MySSH again.
    /// The caller then quits normally (sessions and the MCP server close first).</summary>
    void ApplyAfterExit();
}

/// <summary>Velopack with the GitHub releases of the MySSH repository as the feed.</summary>
public sealed class VelopackUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/dollarkillerx/MySSH";

    readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    UpdateInfo? _update;

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppVersion.Current;
    public string? PendingVersion => _manager.UpdatePendingRestart?.Version.ToString();

    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _update = await _manager.CheckForUpdatesAsync().WaitAsync(ct);
        return _update?.TargetFullRelease.Version.ToString();
    }

    public Task DownloadAsync(Action<int> progress, CancellationToken ct) =>
        _update is null ? Task.CompletedTask : _manager.DownloadUpdatesAsync(_update, progress, ct);

    public void ApplyAfterExit() =>
        _manager.WaitExitThenApplyUpdates(_update?.TargetFullRelease ?? _manager.UpdatePendingRestart, silent: false, restart: true);
}

/// <summary>Not a packaged build (tests, headless, `dotnet run`).</summary>
public sealed class NullAppUpdater : IAppUpdater
{
    public static readonly NullAppUpdater Instance = new();
    public bool IsInstalled => false;
    public string CurrentVersion => AppVersion.Current;
    public string? PendingVersion => null;
    public Task<string?> CheckAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task DownloadAsync(Action<int> progress, CancellationToken ct) => Task.CompletedTask;
    public void ApplyAfterExit() { }
}

public static class AppVersion
{
    /// <summary>"0.2.0" (Directory.Build.props &lt;Version&gt; without the prerelease / build suffix).</summary>
    public static string Current { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
