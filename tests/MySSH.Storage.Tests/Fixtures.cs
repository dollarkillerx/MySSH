using System.Security.Cryptography;
using System.Text;

namespace MySSH.Storage.Tests;

/// <summary>
/// The committed legacy fixture set (tests/fixtures/legacy). Generated from the format spec by <see cref="LegacyFormat"/>
/// with deterministic key and nonces, so <see cref="FixtureGenerationTests"/> can prove the committed bytes match.
/// Set MYSSH_REGENERATE_FIXTURES=1 to rewrite them.
/// </summary>
public static class Fixtures
{
    public const string BastionId = "11111111-1111-4111-8111-111111111111";
    public const string ProdCnId = "22222222-2222-4222-8222-222222222222";
    public const string KeyPassId = "33333333-3333-4333-8333-333333333333";
    public const string KeyNoPassId = "44444444-4444-4444-8444-444444444444";
    public const string SharedAId = "55555555-5555-4555-8555-555555555555";
    public const string SharedBId = "66666666-6666-4666-8666-666666666666";
    public const string InternalDbId = "77777777-7777-4777-8777-777777777777";
    public const string CorruptedId = "88888888-8888-4888-8888-888888888888";

    // Not real keys: the importer treats key material as opaque text.
    public const string KeyA = "-----BEGIN TEST KEY-----\nZml4dHVyZS1rZXktQQ==\n-----END TEST KEY-----\n";
    public const string KeyB = "-----BEGIN TEST KEY-----\nZml4dHVyZS1rZXktQg==\n-----END TEST KEY-----\n";
    public const string KeyC = "-----BEGIN TEST KEY-----\nZml4dHVyZS1rZXktQw==\n-----END TEST KEY-----\n";
    public const string KeyD = "-----BEGIN TEST KEY-----\nZml4dHVyZS1rZXktRA==\n-----END TEST KEY-----\n";

    public const string UnicodeName = "生产服务器 🚀 本番";
    public const string UnicodeNotes = "Ünïcødé notes · 日本語 · 中文";

    public static readonly LegacyProxyConfig Socks = new("socks5", "proxy.example.com", 1080, "puser", "ppass");
    public static readonly LegacyProxyConfig Http = new("http", "http-proxy.example.com", 3128, null, null);

    public static byte[] Key => SHA256.HashData(Encoding.UTF8.GetBytes("myssh legacy fixture key (not secret)"));

    public static IReadOnlyList<LegacyServerConfig> AppDataServers =>
    [
        new(BastionId, "Bastion", "bastion.example.com", 22, "root", new LegacyPasswordAuth("hunter2"), CreatedAt: 1_700_000_001, UpdatedAt: 1_700_000_101),
        new(ProdCnId, UnicodeName, "10.0.0.2", 2222, "root", new LegacyPasswordAuth("hunter2"), Notes: UnicodeNotes),
        new(KeyPassId, "Key with passphrase", "kp.example.com", 22, "alice", new LegacyKeyAuth(KeyA, "correct horse"), Proxy: Socks),
        new(KeyNoPassId, "Key without passphrase", "knp.example.com", 22, "bob", new LegacyKeyAuth(KeyB, null), Proxy: Http),
        new(SharedAId, "Shared key A", "a.example.com", 22, "deploy", new LegacyKeyAuth(KeyC, null)),
        new(SharedBId, "Shared key B", "b.example.com", 22, "deploy", new LegacyKeyAuth(KeyC, null), Proxy: Socks),
        new(InternalDbId, "Internal DB (via bastion)", "10.0.1.5", 5432, "postgres", new LegacyPasswordAuth("db-secret"), JumpHost: BastionId),
        new(CorruptedId, "Corrupted record", "broken.example.com", 22, "x", new LegacyPasswordAuth("will-not-decrypt")),
    ];

    public static readonly HashSet<string> CorruptIds = [CorruptedId];

    public const string V1PasswordHostId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    public const string V1KeyHostId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    public const string V1JumpedHostId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    public const string V1Password = "fixture-backup-password";

    public static IReadOnlyList<LegacyServerConfig> V1Servers =>
    [
        new(V1PasswordHostId, "V1 password host", "v1-pw.example.com", 22, "admin", new LegacyPasswordAuth("v1-pass")),
        new(V1KeyHostId, "V1 key host · ключ", "v1-key.example.com", 2200, "ops", new LegacyKeyAuth(KeyD, "v1-phrase"),
            Proxy: new LegacyProxyConfig("http", "corp-proxy.example.com", 8080, "corp", "corp-pass")),
        new(V1JumpedHostId, "V1 behind jump", "10.9.0.3", 22, "admin", new LegacyPasswordAuth("v1-pass"), JumpHost: V1PasswordHostId, Notes: "via v1 jump"),
    ];

    public static byte[] V1Nonce => SHA256.HashData("myssh v1 fixture nonce"u8.ToArray())[..12];

    public static string AppDataDir => Path.Combine(T.FixturesDir, "appdata", "myssh");
    public static string BackupV1Path => Path.Combine(T.FixturesDir, "backup-v1.txt");

    public static byte[] GenerateServersJson() => LegacyFormat.ServersJson(AppDataServers, Key, new LegacyFormat.NonceSource("appdata"), CorruptIds);

    public static string GenerateBackupV1() => LegacyFormat.BackupV1(V1Servers, V1Password, V1Nonce);
}

public class FixtureGenerationTests
{
    [Fact]
    public void Committed_fixtures_match_the_spec_generator()
    {
        var servers = Fixtures.GenerateServersJson();
        var backup = Fixtures.GenerateBackupV1();
        if (Environment.GetEnvironmentVariable("MYSSH_REGENERATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Fixtures.AppDataDir);
            File.WriteAllBytes(Path.Combine(Fixtures.AppDataDir, "servers.json"), servers);
            File.WriteAllBytes(Path.Combine(Fixtures.AppDataDir, ".key"), Fixtures.Key);
            File.WriteAllText(Fixtures.BackupV1Path, backup);
        }

        Assert.Equal(servers, File.ReadAllBytes(Path.Combine(Fixtures.AppDataDir, "servers.json")));
        Assert.Equal(Fixtures.Key, File.ReadAllBytes(Path.Combine(Fixtures.AppDataDir, ".key")));
        Assert.Equal(backup, File.ReadAllText(Fixtures.BackupV1Path));
    }

    [Fact]
    public void Servers_json_has_serde_pretty_shape()
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(Fixtures.AppDataDir, "servers.json")));
        Assert.StartsWith("{\n  \"" + Fixtures.BastionId + "\": {\n    \"id\": \"" + Fixtures.BastionId + "\",\n    \"name\": \"Bastion\",", text, StringComparison.Ordinal);
        Assert.Contains("\"auth\": {\n      \"type\": \"Password\",\n      \"value\": \"", text, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"PrivateKey\",\n      \"value\": {\n        \"key\": \"", text, StringComparison.Ordinal);
        Assert.Contains("\"proxy_type\": \"socks5\"", text, StringComparison.Ordinal);
        Assert.Contains("\"jump_host\": \"" + Fixtures.BastionId + "\"", text, StringComparison.Ordinal);
        Assert.Contains(Fixtures.UnicodeName, text, StringComparison.Ordinal);
        Assert.False(text.EndsWith('\n'));
    }
}
