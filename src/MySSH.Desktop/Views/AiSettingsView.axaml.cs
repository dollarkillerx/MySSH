using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MySSH.Ai;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

/// <summary>"AI assistant" tab: providers and behaviour (<see cref="AiSettingsViewModel"/>).</summary>
public sealed partial class AiSettingsView : UserControl
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(AiProviderRowViewModel))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(AiEffortOption))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(AiThinkingOption))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(AiRetentionOption))]
    public AiSettingsView() => InitializeComponent();

    /// <summary>Add-provider menu: the item's Tag is the preset (<see cref="AiProviderKind"/> name).</summary>
    async void OnAddPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<AiProviderKind>(tag, out var kind) && DataContext is AiSettingsViewModel vm)
            await vm.AddProviderAsync(kind);
    }
}
