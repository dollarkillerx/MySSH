using System.Globalization;
using MySSH.Core;

namespace MySSH.Desktop.Services;

/// <summary>
/// Writes unexpected exceptions to &lt;data dir&gt;/logs/crash-*.log (a NativeAOT app only prints them to stderr, which is
/// lost when started from Finder). Never throws. Messages may contain host names / paths, never secrets.
/// </summary>
public static class CrashLog
{
    public static string Directory => DirectoryOverride ?? Path.Combine(AppPaths.Default.DataDirectory, "logs");

    /// <summary>Tests point this at a temp folder so they never write into the user's real logs.</summary>
    internal static string? DirectoryOverride { get; set; }

    /// <returns>The log file path, or null if it could not be written.</returns>
    public static string? Write(Exception? exception, string source)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, $"crash-{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.log");
            File.WriteAllText(path,
                $"MySSH {typeof(CrashLog).Assembly.GetName().Version} ({Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})\n" +
                $"{DateTimeOffset.Now:O} {source}\n\n{exception}\n");
            return path;
        }
#pragma warning disable CA1031 // last-resort logging must never throw
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }
}
