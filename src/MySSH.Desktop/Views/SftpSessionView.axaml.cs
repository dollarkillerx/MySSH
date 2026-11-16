using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MySSH.Desktop.ViewModels;

namespace MySSH.Desktop.Views;

public sealed partial class SftpSessionView : UserControl
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces, typeof(SftpRowViewModel))]
    public SftpSessionView()
    {
        InitializeComponent();
        Files.DoubleTapped += (_, _) => Vm?.OpenCommand.Execute(null);
        Files.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Vm?.OpenCommand.Execute(null); }
            else if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None) { e.Handled = true; Vm?.GoUpCommand.Execute(null); }
            else if (e.Key == Key.Delete || (e.Key == Key.Back && e.KeyModifiers.HasFlag(KeyModifiers.Meta))) { e.Handled = true; Vm?.DeleteCommand.Execute(null); }
            else if (e.Key == Key.F2) { e.Handled = true; Vm?.RenameCommand.Execute(null); }
        };
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
    }

    SftpSessionViewModel? Vm => DataContext as SftpSessionViewModel;

    void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths is { Count: > 0 }) Vm?.UploadPaths(paths);
    }

    void OnErrorClosed(object? sender, RoutedEventArgs e) { if (Vm is not null) Vm.Error = null; }
}
