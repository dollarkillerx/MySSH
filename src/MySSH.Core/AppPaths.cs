namespace MySSH.Core;

/// <summary>Operating-system family used to resolve per-user data locations.</summary>
public enum AppPlatform { MacOS, Windows, Linux }

/// <summary>
/// Where MySSH keeps its data (design §5). The new data directory deliberately does NOT reuse the legacy
/// <c>{data_dir}/myssh</c> name: on case-insensitive file systems (macOS default APFS, Windows NTFS) "MySSH" and
/// "myssh" would be the same directory and the new app would write next to (or over) the legacy files.
/// Every path can be overridden (tests, portable installs) by constructing the record directly.
/// </summary>
public sealed record AppPaths
{
    /// <summary>Name of the new data directory on macOS and Windows.</summary>
    public const string DesktopDirectoryName = "MySSH.Desktop";
    /// <summary>Name of the new data directory on Linux (XDG style, lower case).</summary>
    public const string LinuxDirectoryName = "myssh-desktop";
    /// <summary>Directory name used by the legacy Tauri app under <c>dirs::data_dir()</c>.</summary>
    public const string LegacyDirectoryName = "myssh";

    /// <summary>Directory of the new app (database, vault file, DPAPI key file).</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Directory of the legacy Tauri app (<c>servers.json</c>, <c>.key</c>). Only ever read.</summary>
    public required string LegacyDataDirectory { get; init; }

    public string DatabasePath => Path.Combine(DataDirectory, "myssh.db");
    public string VaultFilePath => Path.Combine(DataDirectory, "vault.json");
    /// <summary>Windows only: DPAPI-protected master key.</summary>
    public string DpapiKeyFilePath => Path.Combine(DataDirectory, "masterkey.dpapi");

    private static readonly Lazy<AppPaths> s_default = new(() => ForCurrentPlatform());

    /// <summary>Paths for the current user on the current OS.</summary>
    public static AppPaths Default => s_default.Value;

    public static AppPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? AppPlatform.Windows
        : OperatingSystem.IsMacOS() ? AppPlatform.MacOS
        : AppPlatform.Linux;

    /// <summary>Resolve paths for the running OS; <paramref name="getEnvironmentVariable"/> and <paramref name="homeDirectory"/> are injectable for tests.</summary>
    public static AppPaths ForCurrentPlatform(Func<string, string?>? getEnvironmentVariable = null, string? homeDirectory = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? roaming = CurrentPlatform == AppPlatform.Windows
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : null;
        return Resolve(CurrentPlatform, env, home, roaming);
    }

    /// <summary>
    /// Pure resolution logic. <paramref name="windowsRoamingAppData"/> is FOLDERID_RoamingAppData (what Rust <c>dirs::data_dir()</c>
    /// returns on Windows); when null, <c>%APPDATA%</c> is used.
    /// </summary>
    public static AppPaths Resolve(AppPlatform platform, Func<string, string?> getEnvironmentVariable, string homeDirectory, string? windowsRoamingAppData = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);

        var baseDir = LegacyDataDirBase(platform, getEnvironmentVariable, homeDirectory, windowsRoamingAppData);
        var newName = platform == AppPlatform.Linux ? LinuxDirectoryName : DesktopDirectoryName;
        return new AppPaths
        {
            DataDirectory = Path.Combine(baseDir, newName),
            LegacyDataDirectory = Path.Combine(baseDir, LegacyDirectoryName),
        };
    }

    /// <summary>Equivalent of Rust <c>dirs::data_dir()</c> (dirs 5.x).</summary>
    public static string LegacyDataDirBase(AppPlatform platform, Func<string, string?> getEnvironmentVariable, string homeDirectory, string? windowsRoamingAppData = null)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        switch (platform)
        {
            case AppPlatform.MacOS:
                return Path.Combine(homeDirectory, "Library", "Application Support");
            case AppPlatform.Windows:
                var roaming = windowsRoamingAppData ?? getEnvironmentVariable("APPDATA");
                return string.IsNullOrEmpty(roaming) ? Path.Combine(homeDirectory, "AppData", "Roaming") : roaming;
            default:
                // dirs: $XDG_DATA_HOME if it is an absolute path, otherwise $HOME/.local/share
                var xdg = getEnvironmentVariable("XDG_DATA_HOME");
                return !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg) && xdg.StartsWith('/')
                    ? xdg
                    : Path.Combine(homeDirectory, ".local", "share");
        }
    }
}
