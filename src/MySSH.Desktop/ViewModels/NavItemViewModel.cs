namespace MySSH.Desktop.ViewModels;

public enum AppPage { Hosts, Identities, KnownHosts, Ai, Settings }

/// <summary>SideNav rail entry: Carbon icon kind + localization key.</summary>
public sealed record NavItemViewModel(AppPage Page, string IconKind, string LabelKey);
