namespace MySSH.Core;

/// <summary>
/// The app's permanent identity. <see cref="BundleId"/> is the macOS CFBundleIdentifier and the base for the Linux
/// desktop-file id and the Windows AppUserModelID. Never change it after a release: preferences, Keychain access rights
/// and notarization records are tied to it.
/// </summary>
public static class AppIdentity
{
    public const string BundleId = "cc.nyxnet.myssh";
    public const string DisplayName = "MySSH";
}
