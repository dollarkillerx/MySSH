using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MySSH.Storage;

/// <summary>
/// macOS: MasterKey stored as a Keychain generic password (kSecClassGenericPassword, service + account) through the
/// Security.framework SecItem API. P/Invoke via [LibraryImport] (NativeAOT friendly).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacKeychainKeyStore : IOsKeyStore
{
    private const string SecurityLib = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint KCFStringEncodingUtf8 = 0x08000100;

    public const int ErrSecSuccess = 0;
    public const int ErrSecItemNotFound = -25300;
    public const int ErrSecDuplicateItem = -25299;
    public const int ErrSecInteractionNotAllowed = -25308;
    public const int ErrSecAuthFailed = -25293;
    public const int ErrSecUserCanceled = -128;

    public MacKeychainKeyStore(string service, string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        ArgumentException.ThrowIfNullOrEmpty(account);
        Service = service;
        Account = account;
    }

    public string Service { get; }
    public string Account { get; }
    /// <summary>Label shown in Keychain Access.</summary>
    public string Label { get; init; } = "MySSH master key";
    /// <summary>When false, reads fail with errSecInteractionNotAllowed instead of showing a Keychain prompt.</summary>
    public bool AllowInteraction { get; init; } = true;

    public string Kind => "macos-keychain";
    public bool IsAvailable => OperatingSystem.IsMacOS();

    public byte[]? Load()
    {
        using var cf = new CfScope();
        var keys = new List<IntPtr> { Sym.KSecClass, Sym.KSecAttrService, Sym.KSecAttrAccount, Sym.KSecReturnData, Sym.KSecMatchLimit };
        var values = new List<IntPtr> { Sym.KSecClassGenericPassword, cf.Str(Service), cf.Str(Account), Sym.KCFBooleanTrue, Sym.KSecMatchLimitOne };
        if (!AllowInteraction && Sym.KSecUseAuthenticationUI != IntPtr.Zero && Sym.KSecUseAuthenticationUIFail != IntPtr.Zero)
        {
            keys.Add(Sym.KSecUseAuthenticationUI);
            values.Add(Sym.KSecUseAuthenticationUIFail);
        }
        var query = cf.Dict(keys, values);
        var status = SecItemCopyMatching(query, out var result);
        if (status == ErrSecItemNotFound)
            return null;
        Check(status, "read");
        try
        {
            var length = (int)CFDataGetLength(result);
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, length);
            return bytes;
        }
        finally
        {
            CFRelease(result);
        }
    }

    public void Save(ReadOnlySpan<byte> key)
    {
        using var cf = new CfScope();
        var data = cf.Data(key);
        var query = cf.Dict(
            [Sym.KSecClass, Sym.KSecAttrService, Sym.KSecAttrAccount],
            [Sym.KSecClassGenericPassword, cf.Str(Service), cf.Str(Account)]);
        var update = cf.Dict([Sym.KSecValueData], [data]);
        var status = SecItemUpdate(query, update);
        if (status == ErrSecItemNotFound)
        {
            var attrs = cf.Dict(
                [Sym.KSecClass, Sym.KSecAttrService, Sym.KSecAttrAccount, Sym.KSecAttrLabel, Sym.KSecValueData],
                [Sym.KSecClassGenericPassword, cf.Str(Service), cf.Str(Account), cf.Str(Label), data]);
            status = SecItemAdd(attrs, IntPtr.Zero);
        }
        Check(status, "write");
    }

    public void Delete()
    {
        using var cf = new CfScope();
        var query = cf.Dict(
            [Sym.KSecClass, Sym.KSecAttrService, Sym.KSecAttrAccount],
            [Sym.KSecClassGenericPassword, cf.Str(Service), cf.Str(Account)]);
        var status = SecItemDelete(query);
        if (status == ErrSecItemNotFound)
            return;
        Check(status, "delete");
    }

    private void Check(int status, string operation)
    {
        if (status == ErrSecSuccess)
            return;
        var hint = status switch
        {
            ErrSecInteractionNotAllowed => " (user interaction required but not allowed)",
            ErrSecAuthFailed => " (authorization failed)",
            ErrSecUserCanceled => " (cancelled by user)",
            ErrSecDuplicateItem => " (duplicate item)",
            _ => "",
        };
        throw new OsKeyStoreException($"Keychain {operation} failed for service '{Service}', account '{Account}': OSStatus {status}{hint}.", status);
    }

    /// <summary>Owns CoreFoundation objects created for one call and releases them together.</summary>
    private sealed class CfScope : IDisposable
    {
        private readonly List<IntPtr> _owned = [];

        public IntPtr Str(string s) => Own(CFStringCreateWithCString(IntPtr.Zero, s, KCFStringEncodingUtf8));

        public unsafe IntPtr Data(ReadOnlySpan<byte> bytes)
        {
            fixed (byte* p = bytes)
                return Own(CFDataCreate(IntPtr.Zero, p, bytes.Length));
        }

        public unsafe IntPtr Dict(IReadOnlyList<IntPtr> keys, IReadOnlyList<IntPtr> values)
        {
            var k = keys.ToArray();
            var v = values.ToArray();
            fixed (IntPtr* pk = k)
            fixed (IntPtr* pv = v)
                return Own(CFDictionaryCreate(IntPtr.Zero, pk, pv, k.Length, Sym.KCFTypeDictionaryKeyCallBacks, Sym.KCFTypeDictionaryValueCallBacks));
        }

        private IntPtr Own(IntPtr p)
        {
            if (p == IntPtr.Zero)
                throw new OsKeyStoreException("CoreFoundation allocation failed.");
            _owned.Add(p);
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _owned)
                CFRelease(p);
            _owned.Clear();
        }
    }

    /// <summary>Exported constants (CFStringRef / CFBooleanRef globals and callback structs).</summary>
    private static class Sym
    {
        private static readonly IntPtr s_security = NativeLibrary.Load(SecurityLib);
        private static readonly IntPtr s_cf = NativeLibrary.Load(CoreFoundationLib);

        public static readonly IntPtr KSecClass = Value(s_security, "kSecClass");
        public static readonly IntPtr KSecClassGenericPassword = Value(s_security, "kSecClassGenericPassword");
        public static readonly IntPtr KSecAttrService = Value(s_security, "kSecAttrService");
        public static readonly IntPtr KSecAttrAccount = Value(s_security, "kSecAttrAccount");
        public static readonly IntPtr KSecAttrLabel = Value(s_security, "kSecAttrLabel");
        public static readonly IntPtr KSecValueData = Value(s_security, "kSecValueData");
        public static readonly IntPtr KSecReturnData = Value(s_security, "kSecReturnData");
        public static readonly IntPtr KSecMatchLimit = Value(s_security, "kSecMatchLimit");
        public static readonly IntPtr KSecMatchLimitOne = Value(s_security, "kSecMatchLimitOne");
        public static readonly IntPtr KSecUseAuthenticationUI = OptionalValue(s_security, "kSecUseAuthenticationUI");
        public static readonly IntPtr KSecUseAuthenticationUIFail = OptionalValue(s_security, "kSecUseAuthenticationUIFail");
        public static readonly IntPtr KCFBooleanTrue = Value(s_cf, "kCFBooleanTrue");
        public static readonly IntPtr KCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(s_cf, "kCFTypeDictionaryKeyCallBacks");
        public static readonly IntPtr KCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(s_cf, "kCFTypeDictionaryValueCallBacks");

        private static IntPtr Value(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));

        private static IntPtr OptionalValue(IntPtr lib, string name) =>
            NativeLibrary.TryGetExport(lib, name, out var address) ? Marshal.ReadIntPtr(address) : IntPtr.Zero;
    }

    [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

    [LibraryImport(CoreFoundationLib)]
    private static unsafe partial IntPtr CFDataCreate(IntPtr alloc, byte* bytes, nint length);

    [LibraryImport(CoreFoundationLib)]
    private static partial nint CFDataGetLength(IntPtr data);

    [LibraryImport(CoreFoundationLib)]
    private static partial IntPtr CFDataGetBytePtr(IntPtr data);

    [LibraryImport(CoreFoundationLib)]
    private static unsafe partial IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr* keys, IntPtr* values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [LibraryImport(CoreFoundationLib)]
    private static partial void CFRelease(IntPtr cf);

    [LibraryImport(SecurityLib)]
    private static partial int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [LibraryImport(SecurityLib)]
    private static partial int SecItemAdd(IntPtr attributes, IntPtr result);

    [LibraryImport(SecurityLib)]
    private static partial int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [LibraryImport(SecurityLib)]
    private static partial int SecItemDelete(IntPtr query);
}
