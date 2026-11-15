using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MySSH.Core;

namespace MySSH.Storage;

/// <summary>
/// serde shape shared by the legacy <c>EncryptedServerConfig</c> (servers.json, secret fields = base64 ciphertext) and the
/// plain <c>ServerConfig</c> (v1 backup payload). See tools/legacy/storage.rs.txt and models.rs.txt.
/// </summary>
internal sealed class LegacyServerJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("auth")] public LegacyAuthJson? Auth { get; set; }
    [JsonPropertyName("proxy")] public LegacyProxyJson? Proxy { get; set; }
    [JsonPropertyName("jump_host")] public string? JumpHost { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
}

/// <summary><c>#[serde(tag = "type", content = "value")]</c>: Password → string, PrivateKey → {key, passphrase}.</summary>
internal sealed class LegacyAuthJson
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

internal sealed class LegacyPrivateKeyJson
{
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("passphrase")] public string? Passphrase { get; set; }
}

internal sealed class LegacyProxyJson
{
    [JsonPropertyName("proxy_type")] public string? ProxyType { get; set; }
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
}

/// <summary>A legacy server with all secrets in plain text.</summary>
internal sealed record LegacyServer(
    string Id,
    string Name,
    string Host,
    int Port,
    string? Username,
    IdentityKind AuthKind,
    string? Password,
    string? PrivateKey,
    string? Passphrase,
    LegacyProxy? Proxy,
    string? JumpHost,
    string? Notes,
    long CreatedAt,
    long UpdatedAt);

internal sealed record LegacyProxy(ProxyKind Kind, string Host, int Port, string? Username, string? Password);

/// <summary>Thrown for one source record that cannot be converted; the record is skipped and reported.</summary>
internal sealed class LegacyRecordException(string message) : Exception(message);

/// <summary>Legacy servers → hosts + deduplicated identities and proxies (design §5.4).</summary>
internal static class LegacyMapper
{
    /// <summary>Decode one record. <paramref name="decrypt"/> turns a stored secret string into plain text (identity for v1 backups).</summary>
    public static LegacyServer Decode(LegacyServerJson j, Func<string, string> decrypt, Func<string, string>? decryptProxyPassword, Action<string>? warn)
    {
        if (string.IsNullOrWhiteSpace(j.Id)) throw new LegacyRecordException("Record has no id.");
        if (string.IsNullOrWhiteSpace(j.Host)) throw new LegacyRecordException("Record has no host.");
        if (j.Port is < 1 or > 65535) throw new LegacyRecordException($"Invalid port {j.Port}.");
        if (j.Auth is null) throw new LegacyRecordException("Record has no auth method.");

        IdentityKind kind;
        string? password = null, key = null, passphrase = null;
        switch (j.Auth.Type)
        {
            case "Password":
                if (j.Auth.Value.ValueKind != JsonValueKind.String)
                    throw new LegacyRecordException("Password auth value is not a string.");
                kind = IdentityKind.Password;
                password = Secret(decrypt, j.Auth.Value.GetString()!, "password");
                break;
            case "PrivateKey":
                if (j.Auth.Value.ValueKind != JsonValueKind.Object)
                    throw new LegacyRecordException("PrivateKey auth value is not an object.");
                var pk = j.Auth.Value.Deserialize(StorageJsonContext.Default.LegacyPrivateKeyJson);
                if (pk?.Key is null) throw new LegacyRecordException("PrivateKey auth has no key.");
                kind = IdentityKind.PrivateKey;
                key = Secret(decrypt, pk.Key, "private key");
                passphrase = pk.Passphrase is null ? null : Secret(decrypt, pk.Passphrase, "key passphrase");
                break;
            default:
                throw new LegacyRecordException($"Unknown auth type '{j.Auth.Type}'.");
        }

        LegacyProxy? proxy = null;
        if (j.Proxy is { } p)
        {
            var pkind = p.ProxyType switch
            {
                "http" => ProxyKind.Http,
                "socks5" => ProxyKind.Socks5,
                _ => throw new LegacyRecordException($"Unknown proxy type '{p.ProxyType}'."),
            };
            if (string.IsNullOrWhiteSpace(p.Host) || p.Port is < 1 or > 65535)
                throw new LegacyRecordException("Proxy has an invalid host or port.");
            string? ppw = null;
            if (p.Password is not null)
            {
                try
                {
                    ppw = (decryptProxyPassword ?? decrypt)(p.Password);
                }
                catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
                {
                    // The legacy app also tolerated this (password → None); keep the host, report it.
                    warn?.Invoke($"Proxy password could not be decrypted ({e.Message}); the proxy was imported without a password.");
                }
            }
            proxy = new LegacyProxy(pkind, p.Host, p.Port, NullIfEmpty(p.Username), ppw);
        }

        return new LegacyServer(j.Id, j.Name ?? "", j.Host, j.Port, NullIfEmpty(j.Username), kind, password, key, passphrase, proxy,
            NullIfEmpty(j.JumpHost), j.Notes, j.CreatedAt, j.UpdatedAt);
    }

    private static string Secret(Func<string, string> decrypt, string stored, string what)
    {
        try
        {
            return decrypt(stored);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new LegacyRecordException($"The {what} could not be decrypted: {e.Message}");
        }
    }

    /// <summary>Builds hosts, deduplicated identities and proxies. Ids are deterministic so a re-run maps to the same records.</summary>
    public static void Map(IReadOnlyList<LegacyServer> servers, ImportBatch batch)
    {
        var ordered = servers.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        var now = DateTimeOffset.UtcNow;

        // legacy id → new host id
        var hostIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var s in ordered)
        {
            if (Guid.TryParse(s.Id, out var g))
            {
                hostIds[s.Id] = g;
            }
            else
            {
                hostIds[s.Id] = DeterministicGuid.Create("legacy-host:" + s.Id);
                batch.Warn(s.Id, s.Name, $"Legacy id is not a UUID; mapped to {hostIds[s.Id]}.");
            }
        }

        // ---- identities: same private key (+ passphrase) → one identity; same username + password → one identity
        var identityGroups = new Dictionary<(IdentityKind, string, string, string), List<LegacyServer>>();
        foreach (var s in ordered)
        {
            var k = s.AuthKind == IdentityKind.Password
                ? (IdentityKind.Password, s.Username ?? "", s.Password ?? "", "")
                : (IdentityKind.PrivateKey, NormalizeKey(s.PrivateKey!), s.Passphrase ?? "", s.Passphrase is null ? "0" : "1");
            if (!identityGroups.TryGetValue(k, out var list))
                identityGroups[k] = list = [];
            list.Add(s);
        }

        var identityOf = new Dictionary<string, Identity>(StringComparer.Ordinal);
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var members in identityGroups.Values.OrderBy(m => m[0].Id, StringComparer.Ordinal))
        {
            var first = members[0];
            var usernames = members.Select(m => m.Username).Distinct().ToList();
            var commonUser = usernames.Count == 1 ? usernames[0] : null;
            string label;
            if (first.AuthKind == IdentityKind.Password)
                label = members.Count == 1 ? $"{first.Username ?? "password"}@{first.Host}" : $"{first.Username ?? "password"} (shared password)";
            else
                label = members.Count == 1 ? $"{DisplayName(first)} key" : $"{commonUser ?? "Shared"} key ({members.Count} hosts)";
            label = Unique(label, usedLabels);

            var identity = new Identity
            {
                Id = DeterministicGuid.Create("legacy-identity:" + first.Id),
                Label = label,
                Username = commonUser,
                Kind = first.AuthKind,
                HasPassphrase = first.Passphrase is not null,
                CreatedAt = members.Select(m => Time(m.CreatedAt, now)).Min(),
                UpdatedAt = members.Select(m => Time(m.UpdatedAt, now)).Max(),
            };
            var secrets = first.AuthKind == IdentityKind.Password
                ? new IdentitySecrets(first.Password, null, null)
                : new IdentitySecrets(null, first.PrivateKey, first.Passphrase);
            batch.Identities.Add((identity, secrets));
            foreach (var m in members)
                identityOf[m.Id] = identity;
        }

        // ---- proxies: identical (type, host, port, username, password) → one profile
        var proxyGroups = new Dictionary<LegacyProxy, List<LegacyServer>>();
        foreach (var s in ordered.Where(s => s.Proxy is not null))
        {
            if (!proxyGroups.TryGetValue(s.Proxy!, out var list))
                proxyGroups[s.Proxy!] = list = [];
            list.Add(s);
        }
        var proxyOf = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var usedProxyLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (proxy, members) in proxyGroups.OrderBy(kv => kv.Value[0].Id, StringComparer.Ordinal))
        {
            var kindName = proxy.Kind == ProxyKind.Socks5 ? "SOCKS5" : "HTTP";
            var profile = new ProxyProfile
            {
                Id = DeterministicGuid.Create("legacy-proxy:" + members[0].Id),
                Label = Unique($"{kindName} {proxy.Host}:{proxy.Port.ToString(CultureInfo.InvariantCulture)}", usedProxyLabels),
                Kind = proxy.Kind,
                Hostname = proxy.Host,
                Port = proxy.Port,
                Username = proxy.Username,
                HasPassword = proxy.Password is not null,
            };
            batch.Proxies.Add((profile, proxy.Password));
            foreach (var m in members)
                proxyOf[m.Id] = profile.Id;
        }

        // ---- hosts
        foreach (var s in ordered)
        {
            var identity = identityOf[s.Id];
            var jumpChain = new List<Guid>();
            if (s.JumpHost is { } jh)
            {
                if (hostIds.TryGetValue(jh, out var jid))
                    jumpChain.Add(jid);
                else if (Guid.TryParse(jh, out var external))
                    jumpChain.Add(external); // may already exist in the database; the writer drops it with a warning if not
                else
                    batch.Warn(s.Id, s.Name, $"Jump host '{jh}' was not found; the jump host was dropped.");
            }
            batch.Hosts.Add(new Host
            {
                Id = hostIds[s.Id],
                Label = DisplayName(s),
                Hostname = s.Host,
                Port = s.Port,
                Username = string.Equals(s.Username, identity.Username, StringComparison.Ordinal) ? null : s.Username,
                IdentityId = identity.Id,
                ProxyId = proxyOf.TryGetValue(s.Id, out var pid) ? pid : null,
                JumpChain = jumpChain,
                AiPolicy = AiPolicy.Off,
                Notes = string.IsNullOrEmpty(s.Notes) ? null : s.Notes,
                CreatedAt = Time(s.CreatedAt, now),
                UpdatedAt = Time(s.UpdatedAt, now),
            });
        }
    }

    private static string DisplayName(LegacyServer s) => string.IsNullOrWhiteSpace(s.Name) ? s.Host : s.Name;

    private static string NormalizeKey(string key) => key.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static DateTimeOffset Time(long unixSeconds, DateTimeOffset fallback) =>
        unixSeconds > 0 && unixSeconds < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds) : fallback;

    private static string Unique(string label, HashSet<string> used)
    {
        var candidate = label;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = $"{label} ({n.ToString(CultureInfo.InvariantCulture)})";
        return candidate;
    }
}
