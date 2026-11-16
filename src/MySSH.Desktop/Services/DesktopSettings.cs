using System.Text.Json.Serialization;
using MySSH.Core;

namespace MySSH.Desktop.Services;

public enum ThemePreference { System, Light, Dark }

/// <summary>UI preferences persisted in the settings table under <see cref="Key"/>.</summary>
public sealed record DesktopSettings
{
    public const string Key = "desktop.preferences";
    /// <summary>Last applied preferences (read by views such as the terminal paste confirmation).</summary>
    public static DesktopSettings Current { get; set; } = new();
    /// <summary>null = follow the OS language.</summary>
    public string? Language { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public bool ConfirmMultilinePaste { get; init; } = true;
    public int TerminalFontSize { get; init; } = 14;
    /// <summary>Look for a new release on GitHub after startup (packaged builds only).</summary>
    public bool CheckForUpdates { get; init; } = true;

    public static async Task<DesktopSettings> LoadAsync(ISettingsStore store, CancellationToken ct = default) =>
        await store.GetAsync(Key, DesktopJsonContext.Default.DesktopSettings, ct) ?? new DesktopSettings();

    public Task SaveAsync(ISettingsStore store, CancellationToken ct = default) =>
        store.SetAsync(Key, this, DesktopJsonContext.Default.DesktopSettings, ct);
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DesktopSettings))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
