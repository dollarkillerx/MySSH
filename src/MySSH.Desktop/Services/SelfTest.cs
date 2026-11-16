using MySSH.Core;
using MySSH.Desktop.Localization;
using MySSH.Storage;

namespace MySSH.Desktop.Services;

/// <summary>
/// `MySSH --self-test &lt;legacyDir&gt;`: headless smoke test for NativeAOT builds (CI). Exercises SQLite, AES-GCM,
/// Argon2, source-generated JSON, the legacy importer and satellite resources in a throwaway directory,
/// using master-password mode so it never touches the OS keystore. Exit code 0 = pass.
/// </summary>
internal static partial class SelfTest
{
    public static async Task<int> RunAsync(string legacyDir)
    {
        var root = Directory.CreateTempSubdirectory("myssh-selftest-").FullName;
        try
        {
            var options = new StorageOptions
            {
                Paths = new AppPaths { DataDirectory = Path.Combine(root, AppPaths.DesktopDirectoryName), LegacyDataDirectory = Path.GetFullPath(legacyDir) },
                UsePlatformKeyStore = false,
                Argon2 = new Argon2Parameters(1024, 1, 1),
            };
            int hosts;
            await using (var session = await AppSession.OpenAsync(options))
            {
                Check(await session.PrepareVaultAsync() == StartupPrompt.CreateMasterPassword, "vault asks for a master password");
                await session.CreateMasterPasswordAsync("self-test");
                var offer = await session.DetectLegacyOfferAsync();
                Check(offer is not null, "legacy data detected");
                var result = await session.ImportLegacyAsync();
                hosts = (await session.Storage.Hosts.ListAsync()).Count;
                Check(hosts > 0 && hosts == result.Hosts.Added, $"imported {hosts} hosts");
                var withKey = (await session.Storage.Identities.ListAsync()).First(i => i.Kind == IdentityKind.PrivateKey);
                Check(await session.Storage.Identities.GetSecretsAsync(withKey.Id) is { PrivateKey.Length: > 0 }, "secrets decrypt");
            }
            await using (var reopened = await AppSession.OpenAsync(options))
            {
                Check(await reopened.PrepareVaultAsync() == StartupPrompt.UnlockMasterPassword, "reopen asks to unlock");
                Check(await reopened.UnlockAsync("self-test") == VaultUnlockResult.Unlocked, "unlock with master password");
                Check((await reopened.Storage.Hosts.ListAsync()).Count == hosts, "data persisted");
                await McpRoundTripAsync(reopened.Storage, root);
                await AiRoundTripAsync(reopened.Storage);
            }
            foreach (var (culture, expected) in new[] { ("en-US", "Hosts"), ("zh-CN", "主机"), ("ja-JP", "ホスト") })
            {
                Loc.Instance.SetCulture(culture);
                Check(Loc.Instance["nav.hosts"] == expected, $"resources {culture}");
            }
            Console.WriteLine("self-test: OK");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"self-test: FAILED: {e}");
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// MCP server + tool layer + audit under NativeAOT (spike G10: JSON metadata problems only show up at runtime): start the
    /// real server on loopback / a temp socket, call list_hosts through the SDK client, check policy filtering and audit.
    /// </summary>
    static async Task McpRoundTripAsync(MySshStorage storage, string root)
    {
        var all = await storage.Hosts.ListAsync();
        var visible = all[0] with { AiPolicy = AiPolicy.Ask };
        await storage.Hosts.SaveAsync(visible);
        await using var connector = new MySSH.Ssh.SshConnector(storage.Hosts, storage.Identities, storage.Proxies, storage.KnownHosts,
            ViewModels.RejectingPrompts.Instance, ViewModels.RejectingPrompts.Instance);
        var tools = MySSH.Tools.ToolServices.Create(storage.Hosts, connector, ViewModels.RejectingPrompts.Instance, storage.AuditLog);
        await using var host = await MySSH.Mcp.McpServerHost.StartAsync(
            new MySSH.Mcp.McpHostOptions { Token = MySSH.Mcp.McpToken.Generate(), LocalSocketPath = Path.Combine(root, "m.sock") },
            tools.Catalog, tools.Invoker);
        Check(host.HttpEndpoint is not null && File.Exists(host.LocalSocketPath), "MCP server started (HTTP + local socket)");
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(new ModelContextProtocol.Client.HttpClientTransport(
            new ModelContextProtocol.Client.HttpClientTransportOptions
            {
                Endpoint = host.HttpEndpoint!,
                TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + host.Token },
            }));
        var listed = await client.ListToolsAsync();
        Check(listed.Count == tools.Catalog.Tools.Count, $"MCP tools/list ({listed.Count} tools)");
        var result = await client.CallToolAsync("list_hosts", new Dictionary<string, object?>());
        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));
        Check(result.IsError != true && text.Contains(visible.Label, StringComparison.Ordinal)
              && all.Skip(1).All(h => !text.Contains(h.Id.ToString(), StringComparison.Ordinal)), "MCP list_hosts honours the AI policy");
        var audit = await storage.AuditLog.QueryAsync(new AuditQuery { ActorPrefix = "mcp:" });
        Check(audit.Any(e => e.Tool == "list_hosts" && e.Decision == AuditDecision.Allowed), "tool call audited");
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException("check failed: " + what);
        Console.WriteLine("  ok  " + what);
    }
}
