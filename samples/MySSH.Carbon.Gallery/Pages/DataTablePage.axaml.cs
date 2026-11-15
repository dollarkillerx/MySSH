using Avalonia.Collections;
using Avalonia.Controls;

namespace MySSH.Carbon.Gallery.Pages;

public sealed record HostRow(string Name, string Address, string User, string Group, string LastConnected);

public partial class DataTablePage : UserControl
{
    internal static IReadOnlyList<HostRow> Sample { get; } =
    [
        new("prod-web-01", "10.0.1.21:22", "deploy", "Production", "2 minutes ago"),
        new("prod-web-02", "10.0.1.22:22", "deploy", "Production", "1 hour ago"),
        new("db-master", "10.0.2.10:22", "postgres", "Production", "Yesterday"),
        new("bastion", "bastion.example.com:2222", "ops", "Infrastructure", "Yesterday"),
        new("staging-api", "10.1.0.5:22", "ubuntu", "Staging", "3 days ago"),
        new("build-agent-3", "192.168.10.33:22", "ci", "CI", "Last week"),
        new("nas", "nas.local:22", "admin", "Home", "Last month"),
    ];

    public DataTablePage()
    {
        InitializeComponent();
        var view = new DataGridCollectionView(Sample.ToList());
        view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(HostRow.Name)));
        Hosts.ItemsSource = view;
        Hosts.SelectedIndex = 2;
        Compact.ItemsSource = Sample;
        DemoStates.Apply(this);
    }
}
