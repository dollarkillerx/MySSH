using System.Text.Json;
using MySSH.Tools;

namespace MySSH.Mcp.Tests;

/// <summary>The real MySSH.Tools catalog published over MCP (the invoker stays fake).</summary>
public sealed class RealCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Real_catalog_is_published_verbatim()
    {
        var catalog = new ToolCatalog();
        await using var host = await McpServerHost.StartAsync(new McpHostOptions { EnableLocalSocket = false }, catalog, new FakeInvoker(), ct: Ct);
        var transport = new ModelContextProtocol.Client.HttpClientTransport(new ModelContextProtocol.Client.HttpClientTransportOptions
        {
            Endpoint = host.HttpEndpoint!,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + host.Token },
        });
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport, TestHost.ClientOptions(null), cancellationToken: Ct);
        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(
            ["list_hosts", "run_command", "sftp_list", "sftp_stat", "sftp_read", "sftp_write", "sftp_mkdir", "sftp_delete", "list_sessions", "read_terminal", "send_to_terminal"],
            tools.Select(t => t.Name));
        foreach (var def in catalog.Tools)
        {
            var tool = tools.Single(t => t.Name == def.Name).ProtocolTool;
            Assert.True(JsonElement.DeepEquals(def.InputSchema, tool.InputSchema), def.Name);
            Assert.Equal(def.ReadOnly, tool.Annotations!.ReadOnlyHint);
            Assert.Equal(def.Destructive, tool.Annotations.DestructiveHint);
        }
        Assert.Equal(catalog.Tools.Count, tools.Count);
    }

    [Fact]
    public void Resources_depend_on_these_catalog_entries()
    {
        var catalog = new ToolCatalog();
        Assert.NotNull(catalog.Find(McpToolAdapter.ListHostsTool));
        var readTerminal = catalog.Find(McpToolAdapter.ReadTerminalTool);
        Assert.NotNull(readTerminal);
        Assert.True(readTerminal.InputSchema.GetProperty("properties").TryGetProperty(McpToolAdapter.ReadTerminalSessionArg, out _));
        Assert.Contains(McpToolAdapter.ReadTerminalSessionArg, readTerminal.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
