using MySSH.Core;

namespace MySSH.Storage.Tests;

public class LegacyImportTests
{
    private static async Task<(TempDir Dir, MySshStorage Storage)> OpenWithFixtureAsync()
    {
        var dir = new TempDir();
        T.CopyDirectory(Fixtures.AppDataDir, dir.AppPaths.LegacyDataDirectory);
        return (dir, await T.OpenUnlockedAsync(dir));
    }

    [Fact]
    public async Task Detect_reports_legacy_data()
    {
        var (dir, s) = await OpenWithFixtureAsync();
        using (dir)
        await using (s)
        {
            var d = await s.LegacyImport.DetectAsync(T.Ct);
            Assert.True(d.CanImport);
            Assert.Equal(8, d.RecordCount);
            Assert.False(d.AlreadyImported);
            Assert.Equal(dir.AppPaths.LegacyDataDirectory, d.Directory);
        }
    }

    [Fact]
    public async Task Import_maps_hosts_identities_and_proxies_with_dedupe()
    {
        var (dir, s) = await OpenWithFixtureAsync();
        using (dir)
        await using (s)
        {
            var before = T.HashDirectory(dir.AppPaths.LegacyDataDirectory);
            var r = await s.LegacyImport.ImportAsync(T.Ct);

            Assert.Equal(ImportSource.LegacyAppData, r.Source);
            Assert.Equal(new EntityImportCounts(7, 0, 0), r.Hosts);
            Assert.Equal(new EntityImportCounts(5, 0, 0), r.Identities);
            Assert.Equal(new EntityImportCounts(2, 0, 0), r.Proxies);
            Assert.Equal(1, r.FailedRecords);
            var issue = Assert.Single(r.Issues);
            Assert.Equal((Fixtures.CorruptedId, ImportIssueSeverity.Error), (issue.RecordId, issue.Severity));
            Assert.Contains("decrypt", issue.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.False(r.IsWeakLegacyFormat);
            Assert.Equal(before, T.HashDirectory(dir.AppPaths.LegacyDataDirectory)); // read-only

            var hosts = (await s.Hosts.ListAsync(T.Ct)).ToDictionary(h => h.Id.ToString());
            Assert.Equal(7, hosts.Count);
            Assert.DoesNotContain(Fixtures.CorruptedId, hosts.Keys);
            Assert.All(hosts.Values, h => Assert.Equal(AiPolicy.Off, h.AiPolicy));

            var bastion = hosts[Fixtures.BastionId];
            Assert.Equal(("Bastion", "bastion.example.com", 22), (bastion.Label, bastion.Hostname, bastion.Port));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_001), bastion.CreatedAt);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_101), bastion.UpdatedAt);

            // Same username + password → one identity.
            var prod = hosts[Fixtures.ProdCnId];
            Assert.Equal(Fixtures.UnicodeName, prod.Label);
            Assert.Equal(Fixtures.UnicodeNotes, prod.Notes);
            Assert.Equal(2222, prod.Port);
            Assert.Equal(bastion.IdentityId, prod.IdentityId);
            var rootId = (await s.Identities.GetAsync(bastion.IdentityId!.Value, T.Ct))!;
            Assert.Equal((IdentityKind.Password, "root", "root (shared password)"), (rootId.Kind, rootId.Username, rootId.Label));
            Assert.Null(bastion.Username); // comes from the identity
            Assert.Equal(new IdentitySecrets("hunter2", null, null), await s.Identities.GetSecretsAsync(rootId.Id, T.Ct));

            // Key with passphrase + socks5 proxy with password.
            var kp = hosts[Fixtures.KeyPassId];
            var kpId = (await s.Identities.GetAsync(kp.IdentityId!.Value, T.Ct))!;
            Assert.Equal((IdentityKind.PrivateKey, true, "alice"), (kpId.Kind, kpId.HasPassphrase, kpId.Username));
            Assert.Equal(new IdentitySecrets(null, Fixtures.KeyA, "correct horse"), await s.Identities.GetSecretsAsync(kpId.Id, T.Ct));
            var socks = (await s.Proxies.GetAsync(kp.ProxyId!.Value, T.Ct))!;
            Assert.Equal((ProxyKind.Socks5, "proxy.example.com", 1080, "puser", true), (socks.Kind, socks.Hostname, socks.Port, socks.Username, socks.HasPassword));
            Assert.Equal("SOCKS5 proxy.example.com:1080", socks.Label);
            Assert.Equal("ppass", await s.Proxies.GetPasswordAsync(socks.Id, T.Ct));

            // Key without passphrase + http proxy without password.
            var knp = hosts[Fixtures.KeyNoPassId];
            var knpId = (await s.Identities.GetAsync(knp.IdentityId!.Value, T.Ct))!;
            Assert.False(knpId.HasPassphrase);
            Assert.Equal(new IdentitySecrets(null, Fixtures.KeyB, null), await s.Identities.GetSecretsAsync(knpId.Id, T.Ct));
            var http = (await s.Proxies.GetAsync(knp.ProxyId!.Value, T.Ct))!;
            Assert.Equal((ProxyKind.Http, null, false), (http.Kind, http.Username, http.HasPassword));

            // Two hosts with the same key → one identity; identical socks proxy → same profile.
            var a = hosts[Fixtures.SharedAId];
            var b = hosts[Fixtures.SharedBId];
            Assert.Equal(a.IdentityId, b.IdentityId);
            Assert.Equal("deploy key (2 hosts)", (await s.Identities.GetAsync(a.IdentityId!.Value, T.Ct))!.Label);
            Assert.Equal(socks.Id, b.ProxyId);
            Assert.Null(a.ProxyId);

            // jump_host → JumpChain.
            var db = hosts[Fixtures.InternalDbId];
            Assert.Equal([bastion.Id], db.JumpChain);
            await Assert.ThrowsAsync<EntityInUseException>(() => s.Hosts.DeleteAsync(bastion.Id, T.Ct));

            Assert.Equal(5, (await s.Identities.ListAsync(T.Ct)).Count);
            Assert.Equal(2, (await s.Proxies.ListAsync(T.Ct)).Count);

            var record = await s.LegacyImport.GetLastImportAsync(T.Ct);
            Assert.NotNull(record);
            Assert.Equal(dir.AppPaths.LegacyDataDirectory, record.SourceDirectory);
            Assert.Equal(7, record.Result!.Hosts.Added);
            Assert.Single(record.Result.Issues);
            Assert.True((await s.LegacyImport.DetectAsync(T.Ct)).AlreadyImported);
        }
    }

    [Fact]
    public async Task Import_is_idempotent_and_does_not_clobber_edits()
    {
        var (dir, s) = await OpenWithFixtureAsync();
        using (dir)
        await using (s)
        {
            await s.LegacyImport.ImportAsync(T.Ct);
            var bastion = (await s.Hosts.GetAsync(Guid.Parse(Fixtures.BastionId), T.Ct))!;
            await s.Hosts.SaveAsync(bastion with { Label = "Edited by user" }, T.Ct);

            var again = await s.LegacyImport.ImportAsync(T.Ct);
            Assert.Equal(new EntityImportCounts(0, 0, 7), again.Hosts);
            Assert.Equal(new EntityImportCounts(0, 0, 5), again.Identities);
            Assert.Equal(new EntityImportCounts(0, 0, 2), again.Proxies);
            Assert.Equal(7, (await s.Hosts.ListAsync(T.Ct)).Count);
            Assert.Equal(5, (await s.Identities.ListAsync(T.Ct)).Count);
            Assert.Equal(2, (await s.Proxies.ListAsync(T.Ct)).Count);
            Assert.Equal("Edited by user", (await s.Hosts.GetAsync(bastion.Id, T.Ct))!.Label);
        }
    }

    [Fact]
    public async Task Missing_or_short_key_file_is_a_clear_error()
    {
        var (dir, s) = await OpenWithFixtureAsync();
        using (dir)
        await using (s)
        {
            var keyPath = Path.Combine(dir.AppPaths.LegacyDataDirectory, ".key");
            await File.WriteAllBytesAsync(keyPath, new byte[16], T.Ct);
            var shortKey = await Assert.ThrowsAsync<LegacyImportException>(() => s.LegacyImport.ImportAsync(T.Ct));
            Assert.Equal(LegacyImportError.KeyFileInvalid, shortKey.Error);
            Assert.Contains("32 bytes", shortKey.Message, StringComparison.Ordinal);
            Assert.False((await s.LegacyImport.DetectAsync(T.Ct)).CanImport);

            File.Delete(keyPath);
            var missing = await Assert.ThrowsAsync<LegacyImportException>(() => s.LegacyImport.ImportAsync(T.Ct));
            Assert.Equal(LegacyImportError.KeyFileMissing, missing.Error);

            File.Delete(Path.Combine(dir.AppPaths.LegacyDataDirectory, "servers.json"));
            Assert.Equal(LegacyImportError.ServersFileMissing, (await Assert.ThrowsAsync<LegacyImportException>(() => s.LegacyImport.ImportAsync(T.Ct))).Error);
            Assert.Empty(await s.Hosts.ListAsync(T.Ct));
            Assert.Null(await s.LegacyImport.GetLastImportAsync(T.Ct));
        }
    }

    [Fact]
    public async Task Wrong_key_reports_every_record_and_imports_nothing_fatal()
    {
        var (dir, s) = await OpenWithFixtureAsync();
        using (dir)
        await using (s)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir.AppPaths.LegacyDataDirectory, ".key"), new byte[32], T.Ct);
            var r = await s.LegacyImport.ImportAsync(T.Ct);
            Assert.Equal(8, r.FailedRecords);
            Assert.Equal(0, r.Hosts.Added);
            Assert.All(r.Issues, i => Assert.Equal(ImportIssueSeverity.Error, i.Severity));
        }
    }

    [Fact]
    public async Task Jump_to_a_skipped_record_and_bad_proxy_password_become_warnings()
    {
        using var dir = new TempDir();
        var key = new byte[32];
        key[0] = 7;
        var jumpTarget = "aaaaaaaa-0000-4000-8000-000000000001";
        var dependent = "aaaaaaaa-0000-4000-8000-000000000002";
        var withProxy = "aaaaaaaa-0000-4000-8000-000000000003";
        LegacyFormat.WriteAppData(dir.AppPaths.LegacyDataDirectory,
        [
            new(jumpTarget, "broken jump", "j.example.com", 22, "u", new LegacyPasswordAuth("secret-1")),
            new(dependent, "dependent", "d.example.com", 22, "u", new LegacyPasswordAuth("secret-2"), JumpHost: jumpTarget),
            new(withProxy, "proxy", "p.example.com", 22, "u", new LegacyPasswordAuth("secret-3"),
                Proxy: new LegacyProxyConfig("socks5", "px", 1080, "pu", "pp")),
        ], key, "warnings", corrupt: new HashSet<string> { jumpTarget });

        // Corrupt the proxy password of the third record too.
        var path = Path.Combine(dir.AppPaths.LegacyDataDirectory, "servers.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, T.Ct))!;
        json[withProxy]!["proxy"]!["password"] = Convert.ToBase64String(new byte[40]);
        await File.WriteAllTextAsync(path, json.ToJsonString(), T.Ct);

        await using var s = await T.OpenUnlockedAsync(dir);
        var r = await s.LegacyImport.ImportAsync(T.Ct);
        Assert.Equal(1, r.FailedRecords);
        Assert.Equal(2, r.Hosts.Added);
        Assert.Contains(r.Issues, i => i.RecordId == dependent && i.Severity == ImportIssueSeverity.Warning && i.Reason.Contains("Jump host", StringComparison.Ordinal));
        Assert.Contains(r.Issues, i => i.RecordId == withProxy && i.Severity == ImportIssueSeverity.Warning && i.Reason.Contains("Proxy password", StringComparison.Ordinal));
        Assert.Empty((await s.Hosts.GetAsync(Guid.Parse(dependent), T.Ct))!.JumpChain);
        var proxyHost = (await s.Hosts.GetAsync(Guid.Parse(withProxy), T.Ct))!;
        Assert.False((await s.Proxies.GetAsync(proxyHost.ProxyId!.Value, T.Ct))!.HasPassword);
    }

    [Fact]
    public async Task Malformed_record_is_skipped_not_fatal()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.AppPaths.LegacyDataDirectory);
        var key = new byte[32];
        var good = "bbbbbbbb-0000-4000-8000-000000000001";
        var servers = System.Text.Encoding.UTF8.GetString(LegacyFormat.ServersJson(
            [new LegacyServerConfig(good, "good", "g.example.com", 22, "u", new LegacyPasswordAuth("pw"))], key, new LegacyFormat.NonceSource("m")));
        servers = servers.TrimEnd('}') + ",\n  \"weird\": { \"id\": \"weird\", \"host\": \"w\", \"port\": 22, \"auth\": { \"type\": \"Kerberos\", \"value\": 1 } },\n  \"null\": null\n}";
        await File.WriteAllTextAsync(Path.Combine(dir.AppPaths.LegacyDataDirectory, "servers.json"), servers, T.Ct);
        await File.WriteAllBytesAsync(Path.Combine(dir.AppPaths.LegacyDataDirectory, ".key"), key, T.Ct);

        await using var s = await T.OpenUnlockedAsync(dir);
        var r = await s.LegacyImport.ImportAsync(T.Ct);
        Assert.Equal(1, r.Hosts.Added);
        Assert.Equal(2, r.FailedRecords);
        Assert.Contains(r.Issues, i => i.RecordId == "weird" && i.Reason.Contains("Kerberos", StringComparison.Ordinal));
    }
}
