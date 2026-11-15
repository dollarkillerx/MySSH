using System.Buffers.Binary;
using System.Text;
using MySSH.Core;

namespace MySSH.Storage.Tests;

public class BackupTests
{
    [Fact]
    public async Task V1_legacy_backup_imports_with_ids_and_weak_flag()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var bytes = await File.ReadAllBytesAsync(Fixtures.BackupV1Path, T.Ct);
        Assert.Equal(BackupFormat.LegacyV1, BackupService.DetectFormat(bytes));

        var r = await s.Backup.ImportAsync(new MemoryStream(bytes), Fixtures.V1Password, T.Ct);
        Assert.Equal(ImportSource.LegacyBackupV1, r.Source);
        Assert.True(r.IsWeakLegacyFormat);
        Assert.Equal(new EntityImportCounts(3, 0, 0), r.Hosts);
        Assert.Equal(new EntityImportCounts(2, 0, 0), r.Identities); // admin/v1-pass shared by two hosts
        Assert.Equal(new EntityImportCounts(1, 0, 0), r.Proxies);
        Assert.Empty(r.Issues);

        var pwHost = (await s.Hosts.GetAsync(Guid.Parse(Fixtures.V1PasswordHostId), T.Ct))!;
        var keyHost = (await s.Hosts.GetAsync(Guid.Parse(Fixtures.V1KeyHostId), T.Ct))!;
        var jumped = (await s.Hosts.GetAsync(Guid.Parse(Fixtures.V1JumpedHostId), T.Ct))!;
        Assert.Equal("V1 key host · ключ", keyHost.Label);
        Assert.Equal(2200, keyHost.Port);
        Assert.Equal([pwHost.Id], jumped.JumpChain);
        Assert.Equal("via v1 jump", jumped.Notes);
        Assert.Equal(pwHost.IdentityId, jumped.IdentityId);
        Assert.Equal(new IdentitySecrets("v1-pass", null, null), await s.Identities.GetSecretsAsync(pwHost.IdentityId!.Value, T.Ct));
        Assert.Equal(new IdentitySecrets(null, Fixtures.KeyD, "v1-phrase"), await s.Identities.GetSecretsAsync(keyHost.IdentityId!.Value, T.Ct));
        var proxy = (await s.Proxies.GetAsync(keyHost.ProxyId!.Value, T.Ct))!;
        Assert.Equal((ProxyKind.Http, "corp-proxy.example.com", 8080, "corp"), (proxy.Kind, proxy.Hostname, proxy.Port, proxy.Username));
        Assert.Equal("corp-pass", await s.Proxies.GetPasswordAsync(proxy.Id, T.Ct));

        // Merge by id: re-importing updates instead of duplicating.
        var again = await s.Backup.ImportAsync(new MemoryStream(bytes), Fixtures.V1Password, T.Ct);
        Assert.Equal(new EntityImportCounts(0, 3, 0), again.Hosts);
        Assert.Equal(3, (await s.Hosts.ListAsync(T.Ct)).Count);
    }

    [Fact]
    public async Task V1_wrong_password_is_rejected()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var bytes = await File.ReadAllBytesAsync(Fixtures.BackupV1Path, T.Ct);
        var ex = await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream(bytes), "wrong", T.Ct));
        Assert.Equal(BackupError.WrongPasswordOrCorrupt, ex.Error);
        Assert.Empty(await s.Hosts.ListAsync(T.Ct));
    }

    private static async Task<(Host Bastion, Host Web, Identity Key, ProxyProfile Proxy)> PopulateAsync(MySshStorage s)
    {
        var group = new HostGroup { Name = "prod" };
        var sub = new HostGroup { Name = "db", ParentId = group.Id, Sort = 2 };
        await s.Groups.SaveAsync(group, T.Ct);
        await s.Groups.SaveAsync(sub, T.Ct);
        var key = new Identity { Label = "deploy key", Username = "deploy", Kind = IdentityKind.PrivateKey };
        var pw = new Identity { Label = "root pw", Username = "root", Kind = IdentityKind.Password };
        await s.Identities.SaveAsync(key, new IdentitySecrets(null, Fixtures.KeyA, "phrase ✓"), T.Ct);
        await s.Identities.SaveAsync(pw, new IdentitySecrets("hunter2", null, null), T.Ct);
        var proxy = new ProxyProfile { Label = "corp", Kind = ProxyKind.Socks5, Hostname = "proxy", Port = 1080, Username = "u" };
        await s.Proxies.SaveAsync(proxy, "proxy-pw", T.Ct);
        var bastion = T.NewHost("bastion", "bastion.example.com") with { IdentityId = pw.Id, GroupId = group.Id };
        var web = T.NewHost("web 本番", "web.example.com") with
        {
            IdentityId = key.Id, GroupId = sub.Id, Tags = ["a", "b"], ProxyId = proxy.Id, JumpChain = [bastion.Id],
            AiPolicy = AiPolicy.ReadOnly, AiIdentityId = pw.Id, Notes = "n", Port = 2200, Encoding = "gbk", KeepAliveSeconds = 5,
        };
        await s.Hosts.SaveAsync(bastion, T.Ct);
        await s.Hosts.SaveAsync(web, T.Ct);
        await s.KnownHosts.UpsertAsync(new KnownHostEntry { HostPort = "bastion.example.com", KeyType = "ssh-ed25519", Sha256Fingerprint = "SHA256:x", PublicKeyBase64 = "AAAA" }, T.Ct);
        await s.Settings.SetJsonAsync("ui.theme", "\"g100\"", T.Ct);
        return (bastion, web, key, proxy);
    }

    [Fact]
    public async Task V2_round_trip_restores_everything_into_a_vault_with_a_different_key()
    {
        using var dirA = new TempDir();
        using var dirB = new TempDir();
        var output = new MemoryStream();
        Host bastion, web;
        Identity key;
        ProxyProfile proxy;
        IReadOnlyList<Host> hostsA;
        await using (var a = await T.OpenUnlockedAsync(dirA))
        {
            (bastion, web, key, proxy) = await PopulateAsync(a);
            hostsA = await a.Hosts.ListAsync(T.Ct);
            var export = await a.Backup.ExportAsync(output, "backup pw", ct: T.Ct);
            Assert.Equal((2, 2, 1, 2, 1, 1), (export.Hosts, export.Identities, export.Proxies, export.Groups, export.KnownHosts, export.Settings));
            Assert.Empty(export.OmittedSecrets);
        }

        var bytes = output.ToArray();
        Assert.Equal("MYSSHBK"u8.ToArray(), bytes[..7]);
        Assert.Equal(2, bytes[7]);
        Assert.Equal(T.FastArgon2, BackupService.ReadV2Parameters(bytes));
        Assert.True(bytes.AsSpan().IndexOf("hunter2"u8) < 0);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("bastion.example.com")) < 0); // metadata encrypted too

        await using var b = await T.OpenUnlockedAsync(dirB);
        var r = await b.Backup.ImportAsync(new MemoryStream(bytes), "backup pw", T.Ct);
        Assert.Equal(ImportSource.BackupV2, r.Source);
        Assert.False(r.IsWeakLegacyFormat);
        Assert.Empty(r.Issues);
        Assert.Equal(new EntityImportCounts(2, 0, 0), r.Hosts);

        var hostsB = await b.Hosts.ListAsync(T.Ct);
        Assert.Equal(hostsA.Count, hostsB.Count);
        foreach (var h in hostsA)
        {
            var hb = hostsB.Single(x => x.Id == h.Id);
            Assert.Equal(h.Tags, hb.Tags);
            Assert.Equal(h.JumpChain, hb.JumpChain);
            Assert.Equal(h with { Tags = hb.Tags, JumpChain = hb.JumpChain }, hb);
        }
        Assert.Equal(new IdentitySecrets(null, Fixtures.KeyA, "phrase ✓"), await b.Identities.GetSecretsAsync(key.Id, T.Ct));
        Assert.Equal("hunter2", (await b.Identities.GetSecretsAsync(bastion.IdentityId!.Value, T.Ct))!.Password);
        Assert.Equal("proxy-pw", await b.Proxies.GetPasswordAsync(proxy.Id, T.Ct));
        Assert.Equal(2, (await b.Groups.ListAsync(T.Ct)).Count);
        Assert.Equal("SHA256:x", Assert.Single(await b.KnownHosts.ListAsync(T.Ct)).Sha256Fingerprint);
        Assert.Equal("\"g100\"", await b.Settings.GetJsonAsync("ui.theme", T.Ct));
        await Assert.ThrowsAsync<EntityInUseException>(() => b.Hosts.DeleteAsync(bastion.Id, T.Ct));
    }

    [Fact]
    public async Task V2_import_merges_by_id()
    {
        using var dirA = new TempDir();
        using var dirB = new TempDir();
        var output = new MemoryStream();
        Host web;
        await using (var a = await T.OpenUnlockedAsync(dirA))
        {
            (_, web, _, _) = await PopulateAsync(a);
            await a.Backup.ExportAsync(output, "pw", new BackupExportOptions { IncludeAiProviderKeys = false }, T.Ct);
        }
        await using var b = await T.OpenUnlockedAsync(dirB);
        var unrelated = T.NewHost("unrelated");
        await b.Hosts.SaveAsync(unrelated, T.Ct);
        var r1 = await b.Backup.ImportAsync(new MemoryStream(output.ToArray()), "pw", T.Ct);
        Assert.Equal(new EntityImportCounts(2, 0, 0), r1.Hosts);
        await b.Hosts.SaveAsync((await b.Hosts.GetAsync(web.Id, T.Ct))! with { Label = "local edit" }, T.Ct);

        var r2 = await b.Backup.ImportAsync(new MemoryStream(output.ToArray()), "pw", T.Ct);
        Assert.Equal(new EntityImportCounts(0, 2, 0), r2.Hosts);
        Assert.Equal(new EntityImportCounts(0, 2, 0), r2.Identities);
        Assert.Equal(web.Label, (await b.Hosts.GetAsync(web.Id, T.Ct))!.Label); // backup wins for same id
        Assert.NotNull(await b.Hosts.GetAsync(unrelated.Id, T.Ct)); // others kept
        Assert.Equal(3, (await b.Hosts.ListAsync(T.Ct)).Count);
    }

    [Fact]
    public async Task V2_wrong_password_tampering_and_unsafe_params_are_rejected()
    {
        using var dir = new TempDir();
        var output = new MemoryStream();
        await using var s = await T.OpenUnlockedAsync(dir);
        await PopulateAsync(s);
        await s.Backup.ExportAsync(output, "pw", ct: T.Ct);
        var bytes = output.ToArray();

        Assert.Equal(BackupError.WrongPasswordOrCorrupt,
            (await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream(bytes), "nope", T.Ct))).Error);

        var tamperedNonce = (byte[])bytes.Clone();
        tamperedNonce[40] ^= 1;
        Assert.Equal(BackupError.WrongPasswordOrCorrupt,
            (await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream(tamperedNonce), "pw", T.Ct))).Error);

        var tamperedBody = (byte[])bytes.Clone();
        tamperedBody[^30] ^= 1;
        await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream(tamperedBody), "pw", T.Ct));

        var huge = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(8), 64 * 1024 * 1024); // 64 GiB
        Assert.Equal(BackupError.UnsafeParameters,
            (await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream(huge), "pw", T.Ct))).Error);

        var v3 = (byte[])bytes.Clone();
        v3[7] = 3;
        Assert.Equal(BackupError.UnsupportedVersion,
            (await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream(v3), "pw", T.Ct))).Error);

        Assert.Equal(BackupError.InvalidFormat,
            (await Assert.ThrowsAsync<BackupException>(() => s.Backup.ImportAsync(new MemoryStream("hello"u8.ToArray()), "pw", T.Ct))).Error);
    }

    [Fact]
    public async Task V2_export_uses_configured_default_argon2_parameters()
    {
        using var dir = new TempDir();
        var options = T.Options(dir, new InMemoryKeyStore(), argon: Argon2Parameters.Default);
        await using var s = await MySshStorage.OpenAsync(options, T.Ct);
        await s.Vault.InitializeWithOsKeystoreAsync(ct: T.Ct);
        var output = new MemoryStream();
        await s.Backup.ExportAsync(output, "pw", ct: T.Ct);
        Assert.Equal(new Argon2Parameters(65536, 3, 1), BackupService.ReadV2Parameters(output.ToArray()));
    }

    [Fact]
    public async Task V2_export_omits_undecryptable_secrets_and_reports_them()
    {
        using var dir = new TempDir();
        await using var s = await T.OpenUnlockedAsync(dir);
        var id = new Identity { Label = "x", Kind = IdentityKind.Password };
        await s.Identities.SaveAsync(id, new IdentitySecrets("pw", null, null), T.Ct);
        await T.ExecAsync(s, "UPDATE identities SET password = zeroblob(40);");
        var result = await s.Backup.ExportAsync(new MemoryStream(), "pw", ct: T.Ct);
        Assert.Equal(1, result.Identities);
        Assert.Equal(id.Id, Assert.Single(result.OmittedSecrets).RecordId);
        Assert.Single(await s.UndecryptableRecords.ListAsync(ct: T.Ct));
    }
}
