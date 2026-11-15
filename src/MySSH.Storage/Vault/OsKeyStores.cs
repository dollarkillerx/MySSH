using System.Runtime.Versioning;
using System.Security.Cryptography;
using MySSH.Core;

namespace MySSH.Storage;

/// <summary>
/// Platform secret store that holds the raw 32-byte MasterKey (design §5.1, "OS keystore" protection mode).
/// Implementations: <see cref="MacKeychainKeyStore"/> (macOS), <see cref="DpapiFileKeyStore"/> (Windows).
/// Linux Secret Service is not implemented in P1, so Linux vaults must use the master-password mode.
/// </summary>
public interface IOsKeyStore
{
    /// <summary>Stable identifier persisted in the vault file, e.g. "macos-keychain".</summary>
    string Kind { get; }
    bool IsAvailable { get; }
    /// <summary>Returns the stored key, or null if none is stored.</summary>
    byte[]? Load();
    /// <summary>Creates or replaces the stored key.</summary>
    void Save(ReadOnlySpan<byte> key);
    /// <summary>Removes the stored key; no-op when absent.</summary>
    void Delete();
}

/// <summary>Thrown when the OS keystore reports an error (other than "item not found").</summary>
public sealed class OsKeyStoreException : Exception
{
    public OsKeyStoreException() { }
    public OsKeyStoreException(string message) : base(message) { }
    public OsKeyStoreException(string message, Exception innerException) : base(message, innerException) { }
    public OsKeyStoreException(string message, int status) : base(message) => Status = status;

    /// <summary>Native status code (OSStatus on macOS), 0 when not applicable.</summary>
    public int Status { get; }
}

public static class OsKeyStores
{
    public const string DefaultKeychainService = "MySSH.Desktop";
    public const string DefaultKeychainAccount = "master-key";

    /// <summary>The platform default keystore, or null when the platform has none supported (Linux in P1).</summary>
    public static IOsKeyStore? CreateDefault(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (OperatingSystem.IsMacOS())
            return new MacKeychainKeyStore(DefaultKeychainService, DefaultKeychainAccount);
        if (OperatingSystem.IsWindows())
            return new DpapiFileKeyStore(paths.DpapiKeyFilePath);
        return null;
    }
}

/// <summary>Windows: MasterKey protected with DPAPI (CurrentUser scope) in a small file.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiFileKeyStore(string filePath) : IOsKeyStore
{
    private static readonly byte[] s_entropy = "MySSH.Desktop.MasterKey.v1"u8.ToArray();

    public string Kind => "windows-dpapi";
    public bool IsAvailable => OperatingSystem.IsWindows();
    public string FilePath { get; } = filePath;

    public byte[]? Load()
    {
        if (!File.Exists(FilePath))
            return null;
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(FilePath), s_entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException e)
        {
            throw new OsKeyStoreException($"DPAPI could not unprotect '{FilePath}' (different Windows user or profile?).", e);
        }
    }

    public void Save(ReadOnlySpan<byte> key)
    {
        var plain = key.ToArray();
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, s_entropy, DataProtectionScope.CurrentUser);
            FileUtil.WriteAllBytesAtomic(FilePath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}

internal static class FileUtil
{
    /// <summary>Write to a temp file in the same directory, then rename over the target (owner-only permissions on Unix).</summary>
    public static void WriteAllBytesAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        EnsurePrivateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(fs.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public static void EnsurePrivateDirectory(string dir)
    {
        if (Directory.Exists(dir))
            return;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(dir);
        else
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void RestrictToOwner(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
