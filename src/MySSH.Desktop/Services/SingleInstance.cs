using MySSH.Core;

namespace MySSH.Desktop.Services;

/// <summary>
/// One MySSH per user: an exclusive lock on &lt;data dir&gt;/app.lock held for the process lifetime (flock on Unix, a share
/// lock on Windows; released automatically if the process dies). Lets myssh-mcp start MySSH with --tray without ever
/// opening a second copy, also when the running one has its MCP server switched off.
/// </summary>
public static class SingleInstance
{
    /// <returns>The held lock (keep it alive), or null when another MySSH holds it.</returns>
    public static IDisposable? TryAcquire(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.DataDirectory);
        try
        {
            return new FileStream(Path.Combine(paths.DataDirectory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
