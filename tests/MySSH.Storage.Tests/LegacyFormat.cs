using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MySSH.Storage.Tests;

/// <summary>Legacy (Tauri) auth method: serde <c>#[serde(tag = "type", content = "value")]</c>.</summary>
public abstract record LegacyAuth;
public sealed record LegacyPasswordAuth(string Password) : LegacyAuth;
public sealed record LegacyKeyAuth(string Key, string? Passphrase) : LegacyAuth;

/// <summary>Legacy ProxyConfig; ProxyType is serialized lowercase ("http" | "socks5").</summary>
public sealed record LegacyProxyConfig(string ProxyType, string Host, int Port, string? Username, string? Password);

/// <summary>Legacy ServerConfig (models.rs) in plain text.</summary>
public sealed record LegacyServerConfig(
    string Id, string Name, string Host, int Port, string Username, LegacyAuth Auth,
    LegacyProxyConfig? Proxy = null, string? JumpHost = null, string? Notes = null,
    long CreatedAt = 1_700_000_000, long UpdatedAt = 1_700_000_000);

/// <summary>
/// Writes files exactly in the shape produced by the legacy Rust code (tools/legacy/storage.rs.txt). There is no Rust
/// toolchain here, so this is a re-implementation from the format spec:
/// <list type="bullet">
/// <item>servers.json = <c>serde_json::to_string_pretty(HashMap&lt;String, EncryptedServerConfig&gt;)</c>: 2-space indent,
/// <c>"key": value</c>, struct field order, no trailing newline, non-ASCII written raw.</item>
/// <item>secret field = base64(nonce(12) ‖ AES-256-GCM ciphertext ‖ tag(16)), key = the 32 raw bytes of <c>.key</c>, no AAD.</item>
/// <item>v1 backup = base64(0x01 ‖ nonce(12) ‖ ct ‖ tag), key = SHA-256(UTF-8(password) ‖ "myssh-export-salt-v1"),
/// plaintext = <c>serde_json::to_string(Vec&lt;ServerConfig&gt;)</c> (compact, plain secrets).</item>
/// </list>
/// Nonces come from <see cref="NonceSource"/> so fixture generation is reproducible byte for byte.
/// </summary>
public static class LegacyFormat
{
    public sealed class NonceSource(string seed)
    {
        private int _counter;
        public byte[] Next() => SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:nonce:{_counter++}"))[..12];
    }

    private static JsonWriterOptions Pretty => new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonWriterOptions Compact => new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string EncryptField(byte[] key, string plaintext, NonceSource nonces) =>
        Convert.ToBase64String(FieldCipher.EncryptLegacy(key, Encoding.UTF8.GetBytes(plaintext), nonces.Next()));

    /// <param name="corrupt">Ids whose auth ciphertext gets one byte flipped (GCM tag check then fails).</param>
    public static byte[] ServersJson(IEnumerable<LegacyServerConfig> servers, byte[] key, NonceSource nonces, IReadOnlySet<string>? corrupt = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Pretty))
        {
            w.WriteStartObject();
            foreach (var s in servers)
            {
                string Enc(string plain)
                {
                    var b64 = EncryptField(key, plain, nonces);
                    if (corrupt?.Contains(s.Id) != true) return b64;
                    var bytes = Convert.FromBase64String(b64);
                    bytes[^20] ^= 0x5A; // inside the ciphertext
                    return Convert.ToBase64String(bytes);
                }
                w.WritePropertyName(s.Id);
                WriteServer(w, s, Enc, plain => EncryptField(key, plain, nonces));
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static string BackupV1(IEnumerable<LegacyServerConfig> servers, string password, byte[] nonce)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Compact))
        {
            w.WriteStartArray();
            foreach (var s in servers)
                WriteServer(w, s, p => p, p => p);
            w.WriteEndArray();
        }
        var json = ms.ToArray();
        var key = SHA256.HashData([.. Encoding.UTF8.GetBytes(password), .. "myssh-export-salt-v1"u8.ToArray()]);
        var sealedBytes = FieldCipher.EncryptLegacy(key, json, nonce);
        return Convert.ToBase64String([1, .. sealedBytes]);
    }

    private static void WriteServer(Utf8JsonWriter w, LegacyServerConfig s, Func<string, string> authSecret, Func<string, string> proxySecret)
    {
        w.WriteStartObject();
        Str(w, "id", s.Id);
        Str(w, "name", s.Name);
        Str(w, "host", s.Host);
        w.WriteNumber("port", s.Port);
        Str(w, "username", s.Username);
        w.WritePropertyName("auth");
        w.WriteStartObject();
        switch (s.Auth)
        {
            case LegacyPasswordAuth p:
                Str(w, "type", "Password");
                Str(w, "value", authSecret(p.Password));
                break;
            case LegacyKeyAuth k:
                Str(w, "type", "PrivateKey");
                w.WritePropertyName("value");
                w.WriteStartObject();
                Str(w, "key", authSecret(k.Key));
                if (k.Passphrase is null) w.WriteNull("passphrase");
                else Str(w, "passphrase", authSecret(k.Passphrase));
                w.WriteEndObject();
                break;
        }
        w.WriteEndObject();
        if (s.Proxy is null)
        {
            w.WriteNull("proxy");
        }
        else
        {
            w.WritePropertyName("proxy");
            w.WriteStartObject();
            Str(w, "proxy_type", s.Proxy.ProxyType);
            Str(w, "host", s.Proxy.Host);
            w.WriteNumber("port", s.Proxy.Port);
            if (s.Proxy.Username is null) w.WriteNull("username"); else Str(w, "username", s.Proxy.Username);
            if (s.Proxy.Password is null) w.WriteNull("password"); else Str(w, "password", proxySecret(s.Proxy.Password));
            w.WriteEndObject();
        }
        if (s.JumpHost is null) w.WriteNull("jump_host"); else Str(w, "jump_host", s.JumpHost);
        if (s.Notes is null) w.WriteNull("notes"); else Str(w, "notes", s.Notes);
        w.WriteNumber("created_at", s.CreatedAt);
        w.WriteNumber("updated_at", s.UpdatedAt);
        w.WriteEndObject();
    }

    /// <summary>serde_json string escaping: only '"', '\\' and control characters are escaped; everything else is raw UTF-8.</summary>
    private static void Str(Utf8JsonWriter w, string name, string value)
    {
        w.WritePropertyName(name);
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u00").Append(((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        w.WriteRawValue(sb.Append('"').ToString(), skipInputValidation: true);
    }

    /// <summary>Write servers.json + .key into <paramref name="dir"/>.</summary>
    public static void WriteAppData(string dir, IEnumerable<LegacyServerConfig> servers, byte[] key, string seed, IReadOnlySet<string>? corrupt = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "servers.json"), ServersJson(servers, key, new NonceSource(seed), corrupt));
        File.WriteAllBytes(Path.Combine(dir, ".key"), key);
    }
}
