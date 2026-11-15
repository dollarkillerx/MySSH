using Avalonia.Controls;

namespace MySSH.Spike.McpHost;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public void ShowEndpoint(string endpoint, SpikeOptions o) =>
        Info.Text = $"HTTP: {endpoint}\nUDS: {o.SocketPath}\nendpoint.json: {o.EndpointFile}\nApproval: {(o.AutoApprove is { } a ? (a ? "auto-allow" : "auto-deny") : $"dialog, timeout {o.ApprovalTimeout.TotalSeconds}s")}";
}
