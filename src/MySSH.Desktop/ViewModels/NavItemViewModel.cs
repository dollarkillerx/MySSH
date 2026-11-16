namespace MySSH.Desktop.ViewModels;

public enum AppPage { Hosts, Sessions, Identities, KnownHosts, Ai, Settings }

/// <summary>SideNav rail entry: Carbon icon kind + localization key.</summary>
public sealed record NavItemViewModel(AppPage Page, string IconKind, string LabelKey);

public enum SessionKind { Terminal, Sftp, Rdp }

/// <summary>Pages that arrive in later phases (AI / MCP is P3–P4).</summary>
public sealed record PlaceholderPageViewModel(string Title);
