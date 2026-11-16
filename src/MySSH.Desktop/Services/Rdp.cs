using System.Diagnostics;
using System.Globalization;
using System.Text;
using MySSH.Core;

namespace MySSH.Desktop.Services;

/// <summary>Where the system RDP client should connect (the real host, or the local end of an SSH tunnel).</summary>
public sealed record RdpTarget(string Address, int Port, string? Username, string Title);

/// <summary>
/// Microsoft .rdp file (UTF-8 with BOM, CRLF; read by mstsc, Windows App on macOS, Remmina and FreeRDP). Never contains
/// the password: the user types it or pastes it from MySSH ("Copy password").
/// </summary>
public static class RdpFile
{
    static string Clean(string value) => new([.. value.Where(c => !char.IsControl(c))]);

    public static string Build(RdpTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var address = target.Address.Contains(':', StringComparison.Ordinal) && !target.Address.StartsWith('[') ? $"[{target.Address}]" : target.Address;
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");
        Line($"full address:s:{Clean(address)}:{target.Port.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(target.Username)) Line($"username:s:{Clean(target.Username)}");
        Line("screen mode id:i:1");          // windowed
        Line("use multimon:i:0");
        Line("session bpp:i:32");
        Line("smart sizing:i:1");
        Line("dynamic resolution:i:1");
        Line("redirectclipboard:i:1");
        Line("audiomode:i:0");               // play sound on this computer
        Line("autoreconnection enabled:i:1");
        Line("authentication level:i:2");    // warn on certificate problems, let the user decide
        Line("prompt for credentials:i:0");
        return sb.ToString();
    }

    /// <summary>Writes the file owner-only under &lt;data dir&gt;/rdp (removed again when the tab closes).</summary>
    public static string Write(string directory, string name, RdpTarget target)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var safe = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        var path = Path.Combine(directory, safe + ".rdp");
        File.WriteAllText(path, Build(target), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}

public enum RdpClientKind { Mstsc, WindowsApp, Remmina, FreeRdp }

/// <param name="Command">Executable to start; <paramref name="Arguments"/> get the .rdp path appended where {0} appears.</param>
public sealed record RdpClient(RdpClientKind Kind, string Name, string Command, IReadOnlyList<string> Arguments);

/// <summary>Finds and starts the system remote-desktop client. Process and file probes are injectable for tests.</summary>
public sealed class RdpLauncher
{
    public const string WindowsAppBundleId = "com.microsoft.rdc.macos";
    public static readonly Uri WindowsAppStoreUrl = new("https://apps.apple.com/app/windows-app/id1295203466");

    readonly AppPlatform _platform;
    readonly Func<string, bool> _fileExists;
    readonly Func<string, string?> _which;
    readonly Func<string, IReadOnlyList<string>, bool> _start;

    public RdpLauncher(AppPlatform? platform = null, Func<string, bool>? fileExists = null, Func<string, string?>? which = null,
        Func<string, IReadOnlyList<string>, bool>? start = null)
    {
        _platform = platform ?? (OperatingSystem.IsWindows() ? AppPlatform.Windows : OperatingSystem.IsMacOS() ? AppPlatform.MacOS : AppPlatform.Linux);
        _fileExists = fileExists ?? (p => File.Exists(p) || Directory.Exists(p));
        _which = which ?? Which;
        _start = start ?? StartProcess;
    }

    public AppPlatform Platform => _platform;

    /// <summary>The client that will be used, or null when none is installed.</summary>
    public RdpClient? Detect()
    {
        switch (_platform)
        {
            case AppPlatform.Windows:
                var mstsc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe");
                return new RdpClient(RdpClientKind.Mstsc, "Remote Desktop Connection", _fileExists(mstsc) ? mstsc : "mstsc.exe", ["{0}"]);
            case AppPlatform.MacOS:
                // Windows App (formerly Microsoft Remote Desktop) keeps the same bundle id.
                foreach (var app in new[] { "/Applications/Windows App.app", "/Applications/Microsoft Remote Desktop.app",
                             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Windows App.app") })
                    if (_fileExists(app))
                        return new RdpClient(RdpClientKind.WindowsApp, Path.GetFileNameWithoutExtension(app), "/usr/bin/open", ["-b", WindowsAppBundleId, "{0}"]);
                return null;
            default:
                if (_which("remmina") is { } remmina) return new RdpClient(RdpClientKind.Remmina, "Remmina", remmina, ["-c", "{0}"]);
                foreach (var name in new[] { "xfreerdp3", "xfreerdp", "wlfreerdp3", "wlfreerdp" })
                    if (_which(name) is { } freerdp) return new RdpClient(RdpClientKind.FreeRdp, "FreeRDP", freerdp, ["{0}", "/cert:tofu", "+clipboard", "/dynamic-resolution"]);
                return null;
        }
    }

    /// <returns>The client used, or null when none is installed or it could not be started.</returns>
    public RdpClient? Launch(string rdpFile)
    {
        if (Detect() is not { } client) return null;
        var args = client.Arguments.Select(a => a == "{0}" ? rdpFile : a).ToList();
        return _start(client.Command, args) ? client : null;
    }

    static bool StartProcess(string command, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(command) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
