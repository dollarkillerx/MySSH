using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySSH.Core;

namespace MySSH.Storage;

public enum BackupFormat { Unknown, LegacyV1, V2 }

public enum BackupError { InvalidFormat, UnsupportedVersion, WrongPasswordOrCorrupt, UnsafeParameters }

public sealed class BackupException : Exception
{
    public BackupException() { }
    public BackupException(string message) : base(message) { }
    public BackupException(string message, Exception innerException) : base(message, innerException) { }
    public BackupException(BackupError error, string message, Exception? innerException = null) : base(message, innerException) => Error = error;

    public BackupError Error { get; }
}

public sealed record BackupExportOptions
{
    /// <summary>Include AI provider API keys (design §5: default off). No AI providers exist yet; kept for API stability.</summary>
    public bool IncludeAiProviderKeys { get; init; }
}

public sealed record BackupExportResult(int Hosts, int Identities, int Proxies, int Groups, int KnownHosts, int Settings,
    /// <summary>Secrets that could not be decrypted and were therefore NOT included (record metadata is included).</summary>
    IReadOnlyList<UndecryptableRecord> OmittedSecrets);

/// <summary>
/// Backup v2 (design §5.3): <c>"MYSSHBK" | 0x02 | m_KiB u32le | t u32le | p u32le | salt(16) | nonce(12) | AES-256-GCM(json) | tag(16)</c>.
/// Key = Argon2id(password, salt, m, t, p); AAD = the 48-byte header. Import also accepts the legacy v1 format.
/// </summary>
public sealed class BackupService
{
    internal static ReadOnlySpan<byte> Magic => "MYSSHBK"u8;
    internal const byte Version2 = 2;
    internal const int HeaderSize = 7 + 1 + 12 + Argon2Parameters.SaltSize + FieldCipher.NonceSize; // 48
    private const int MaxBackupBytes = 256 * 1024 * 1024;
    private static readonly string[] s_excludedSettingPrefixes = [LegacyImportService.SettingsKey];

    private readonly StorageDatabase _db;
    private readonly Vault _vault;
    private readonly Argon2Parameters _kdf;

    internal BackupService(StorageDatabase db, Vault vault, Argon2Parameters kdf)
    {
        _db = db;
        _vault = vault;
        _kdf = kdf;
    }

    public static BackupFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic))
            return BackupFormat.V2;
        return TryDecodeV1(data, out _) ? BackupFormat.LegacyV1 : BackupFormat.Unknown;
    }

    /// <summary>Exports all entities including decrypted secrets, encrypted under <paramref name="password"/>. Requires an unlocked vault.</summary>
    public async Task<BackupExportResult> ExportAsync(Stream destination, string password, BackupExportOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(password);
        options ??= new BackupExportOptions();
        _vault.EnsureUnlocked();

        var payload = new BackupPayload { CreatedAt = DateTimeOffset.UtcNow, IncludesAiProviderKeys = options.IncludeAiProviderKeys };
        var omitted = new List<(UndecryptableRecord Record, byte[] Blob)>();
        await using (var conn = await _db.OpenAsync(ct).ConfigureAwait(false))
        {
            foreach (var g in await Rows.ReadGroupsAsync(conn, null, ct).ConfigureAwait(false))
                payload.Groups.Add(new BackupGroup { Id = g.Id, Name = g.Name, ParentId = g.ParentId, Sort = g.Sort });

            foreach (var i in await Rows.ReadIdentitiesAsync(conn, null, null, ct).ConfigureAwait(false))
            {
                var blobs = (await Rows.ReadIdentityBlobsAsync(conn, i.Id, ct).ConfigureAwait(false))!.Value;
                payload.Identities.Add(new BackupIdentity
                {
                    Id = i.Id, Label = i.Label, Username = i.Username, Kind = i.Kind, CreatedAt = i.CreatedAt, UpdatedAt = i.UpdatedAt,
                    Password = TryDecrypt(Rows.IdentitiesTable, i.Id, Rows.FieldPassword, blobs.Password, omitted),
                    PrivateKey = TryDecrypt(Rows.IdentitiesTable, i.Id, Rows.FieldPrivateKey, blobs.PrivateKey, omitted),
                    Passphrase = TryDecrypt(Rows.IdentitiesTable, i.Id, Rows.FieldPassphrase, blobs.Passphrase, omitted),
                });
            }

            foreach (var p in await Rows.ReadProxiesAsync(conn, null, null, ct).ConfigureAwait(false))
            {
                var blob = (byte[]?)await Sql.ScalarAsync(conn, null, "SELECT password FROM proxies WHERE id = $id;", ct, ("$id", Sql.Id(p.Id))).ConfigureAwait(false);
                payload.Proxies.Add(new BackupProxy
                {
                    Id = p.Id, Label = p.Label, Kind = p.Kind, Hostname = p.Hostname, Port = p.Port, Username = p.Username,
                    Password = TryDecrypt(Rows.ProxiesTable, p.Id, Rows.FieldPassword, blob, omitted),
                });
            }

            foreach (var h in await Rows.ReadHostsAsync(conn, null, null, ct).ConfigureAwait(false))
            {
                payload.Hosts.Add(new BackupHost
                {
                    Id = h.Id, Label = h.Label, Hostname = h.Hostname, Port = h.Port, Username = h.Username, IdentityId = h.IdentityId,
                    GroupId = h.GroupId, Tags = [.. h.Tags], ProxyId = h.ProxyId, JumpChain = [.. h.JumpChain],
                    KeepAliveSeconds = h.KeepAliveSeconds, Encoding = h.Encoding, AiPolicy = h.AiPolicy, AiIdentityId = h.AiIdentityId,
                    Notes = h.Notes, CreatedAt = h.CreatedAt, UpdatedAt = h.UpdatedAt,
                });
            }

            foreach (var k in await Rows.ReadKnownHostsAsync(conn, null, null, ct).ConfigureAwait(false))
            {
                payload.KnownHosts.Add(new BackupKnownHost
                {
                    HostPort = k.HostPort, KeyType = k.KeyType, Sha256Fingerprint = k.Sha256Fingerprint,
                    PublicKeyBase64 = k.PublicKeyBase64, FirstSeen = k.FirstSeen, LastSeen = k.LastSeen,
                });
            }

            await using var cmd = Sql.Command(conn, null, "SELECT key, value FROM settings ORDER BY key;");
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = r.GetString(0);
                if (s_excludedSettingPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                    continue;
                using var doc = JsonDocument.Parse(r.GetString(1));
                payload.Settings[key] = doc.RootElement.Clone();
            }
        }

        // Flag what we could not export so the UI shows it consistently.
        if (omitted.Count > 0)
        {
            await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
            foreach (var (o, blob) in omitted)
                await Undecryptable.FlagAsync(conn, null, o.Table, o.RecordId, o.Field, o.Reason, blob, ct).ConfigureAwait(false);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, StorageJsonContext.Default.BackupPayload);
        try
        {
            var bytes = await EncryptV2Async(json, password, _kdf, ct).ConfigureAwait(false);
            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
        return new BackupExportResult(payload.Hosts.Count, payload.Identities.Count, payload.Proxies.Count, payload.Groups.Count,
            payload.KnownHosts.Count, payload.Settings.Count, [.. omitted.Select(o => o.Record)]);
    }

    /// <summary>
    /// Imports a v2 backup or a legacy v1 backup (auto-detected) and merges by id (existing records with the same id are
    /// replaced, others are kept). Requires an unlocked vault.
    /// </summary>
    /// <exception cref="BackupException"/>
    public async Task<ImportResult> ImportAsync(Stream source, string password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(password);
        _vault.EnsureUnlocked();
        var data = await ReadAllAsync(source, ct).ConfigureAwait(false);
        return DetectFormat(data) switch
        {
            BackupFormat.V2 => await ImportV2Async(data, password, ct).ConfigureAwait(false),
            BackupFormat.LegacyV1 => await ImportV1Async(data, password, ct).ConfigureAwait(false),
            _ => throw new BackupException(BackupError.InvalidFormat, "The file is not a MySSH backup."),
        };
    }

    // ---------------- v2 ----------------

    internal static async Task<byte[]> EncryptV2Async(byte[] plaintext, string password, Argon2Parameters kdf, CancellationToken ct)
    {
        var output = new byte[HeaderSize + plaintext.Length + FieldCipher.TagSize];
        Magic.CopyTo(output);
        output[7] = Version2;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), (uint)kdf.MemoryKiB);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), (uint)kdf.Iterations);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), (uint)kdf.Parallelism);
        var salt = output.AsSpan(20, Argon2Parameters.SaltSize);
        var nonce = output.AsSpan(20 + Argon2Parameters.SaltSize, FieldCipher.NonceSize);
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);
        var key = await Argon2Kdf.DeriveKeyAsync(password, salt.ToArray(), kdf, ct).ConfigureAwait(false);
        try
        {
            using var gcm = new AesGcm(key, FieldCipher.TagSize);
            gcm.Encrypt(output.AsSpan(20 + Argon2Parameters.SaltSize, FieldCipher.NonceSize), plaintext,
                output.AsSpan(HeaderSize, plaintext.Length), output.AsSpan(HeaderSize + plaintext.Length, FieldCipher.TagSize),
                output.AsSpan(0, HeaderSize));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return output;
    }

    /// <summary>Reads the Argon2 parameters from a v2 header without decrypting.</summary>
    public static Argon2Parameters ReadV2Parameters(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + FieldCipher.TagSize || !data[..Magic.Length].SequenceEqual(Magic))
            throw new BackupException(BackupError.InvalidFormat, "The file is not a MySSH v2 backup.");
        if (data[7] != Version2)
            throw new BackupException(BackupError.UnsupportedVersion, $"Unsupported backup version {data[7]}.");
        var m = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var t = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var p = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (m > int.MaxValue || t > int.MaxValue || p > int.MaxValue)
            throw new BackupException(BackupError.UnsafeParameters, "Backup KDF parameters are out of range.");
        var kdf = new Argon2Parameters((int)m, (int)t, (int)p);
        if (!kdf.IsWithinSafeBounds)
            throw new BackupException(BackupError.UnsafeParameters, $"Backup KDF parameters {kdf} are outside the accepted range.");
        return kdf;
    }

    internal static async Task<byte[]> DecryptV2Async(byte[] data, string password, CancellationToken ct)
    {
        var kdf = ReadV2Parameters(data);
        var salt = data.AsMemory(20, Argon2Parameters.SaltSize);
        var key = await Argon2Kdf.DeriveKeyAsync(password, salt, kdf, ct).ConfigureAwait(false);
        try
        {
            var ctLen = data.Length - HeaderSize - FieldCipher.TagSize;
            var plain = new byte[ctLen];
            using var gcm = new AesGcm(key, FieldCipher.TagSize);
            gcm.Decrypt(data.AsSpan(20 + Argon2Parameters.SaltSize, FieldCipher.NonceSize), data.AsSpan(HeaderSize, ctLen),
                data.AsSpan(HeaderSize + ctLen, FieldCipher.TagSize), plain, data.AsSpan(0, HeaderSize));
            return plain;
        }
        catch (CryptographicException e)
        {
            throw new BackupException(BackupError.WrongPasswordOrCorrupt, "Wrong backup password, or the backup file is corrupted.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<ImportResult> ImportV2Async(byte[] data, string password, CancellationToken ct)
    {
        var json = await DecryptV2Async(data, password, ct).ConfigureAwait(false);
        BackupPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize(json, StorageJsonContext.Default.BackupPayload)
                      ?? throw new JsonException("empty payload");
        }
        catch (JsonException e)
        {
            throw new BackupException(BackupError.InvalidFormat, $"The backup payload is invalid: {e.Message}", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        var batch = new ImportBatch();
        foreach (var g in payload.Groups.Where(g => !string.IsNullOrWhiteSpace(g.Name)))
            batch.Groups.Add(new HostGroup { Id = g.Id, Name = g.Name ?? "", ParentId = g.ParentId, Sort = g.Sort });
        foreach (var i in payload.Identities)
        {
            if (string.IsNullOrWhiteSpace(i.Label))
            {
                batch.Fail(Sql.Id(i.Id), null, "Identity has no label.");
                continue;
            }
            var hasSecrets = i.Password is not null || i.PrivateKey is not null || i.Passphrase is not null;
            batch.Identities.Add((new Identity
            {
                Id = i.Id, Label = i.Label ?? "", Username = i.Username, Kind = i.Kind, HasPassphrase = i.Passphrase is not null,
                CreatedAt = i.CreatedAt, UpdatedAt = i.UpdatedAt,
            }, hasSecrets ? new IdentitySecrets(i.Password, i.PrivateKey, i.Passphrase) : null));
        }
        foreach (var p in payload.Proxies)
        {
            if (string.IsNullOrWhiteSpace(p.Label) || string.IsNullOrWhiteSpace(p.Hostname) || p.Port is < 1 or > 65535)
            {
                batch.Fail(Sql.Id(p.Id), p.Label, "Proxy has no label, an empty host name or an invalid port.");
                continue;
            }
            batch.Proxies.Add((new ProxyProfile
            {
                Id = p.Id, Label = p.Label ?? "", Kind = p.Kind, Hostname = p.Hostname ?? "", Port = p.Port, Username = p.Username,
                HasPassword = p.Password is not null,
            }, p.Password));
        }
        foreach (var h in payload.Hosts)
        {
            if (string.IsNullOrWhiteSpace(h.Label) || string.IsNullOrWhiteSpace(h.Hostname) || h.Port is < 1 or > 65535)
            {
                batch.Fail(Sql.Id(h.Id), h.Label, "Host has no label, an empty host name or an invalid port.");
                continue;
            }
            batch.Hosts.Add(new Host
            {
                Id = h.Id, Label = h.Label ?? "", Hostname = h.Hostname ?? "", Port = h.Port, Username = h.Username,
                IdentityId = h.IdentityId, GroupId = h.GroupId, Tags = h.Tags ?? [], ProxyId = h.ProxyId, JumpChain = h.JumpChain ?? [],
                KeepAliveSeconds = h.KeepAliveSeconds, Encoding = h.Encoding ?? "utf-8", AiPolicy = h.AiPolicy, AiIdentityId = h.AiIdentityId,
                Notes = h.Notes, CreatedAt = h.CreatedAt, UpdatedAt = h.UpdatedAt,
            });
        }
        foreach (var k in payload.KnownHosts)
        {
            if (string.IsNullOrEmpty(k.HostPort) || string.IsNullOrEmpty(k.KeyType) || string.IsNullOrEmpty(k.Sha256Fingerprint))
            {
                batch.Fail(k.HostPort ?? "", null, "Known host entry is incomplete.");
                continue;
            }
            batch.KnownHosts.Add(new KnownHostEntry
            {
                HostPort = k.HostPort, KeyType = k.KeyType, Sha256Fingerprint = k.Sha256Fingerprint,
                PublicKeyBase64 = k.PublicKeyBase64, FirstSeen = k.FirstSeen, LastSeen = k.LastSeen,
            });
        }
        foreach (var (key, value) in payload.Settings)
        {
            if (s_excludedSettingPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                continue;
            batch.Settings[key] = value.GetRawText();
        }
        return await ImportWriter.WriteAsync(_db, _vault, batch, ImportMode.Overwrite, ImportSource.BackupV2, ct).ConfigureAwait(false);
    }

    // ---------------- v1 (legacy Tauri export) ----------------

    private static bool TryDecodeV1(ReadOnlySpan<byte> data, out byte[] combined)
    {
        combined = [];
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data).Trim();
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (text.Length == 0)
            return false;
        var buffer = new byte[text.Length];
        if (!Convert.TryFromBase64String(text, buffer, out var written) || written < 1 + FieldCipher.NonceSize + FieldCipher.TagSize || buffer[0] != 1)
            return false;
        combined = buffer[..written];
        return true;
    }

    /// <summary>Key derivation of the legacy export: SHA-256(UTF-8(password) ‖ "myssh-export-salt-v1"). Weak (no KDF).</summary>
    internal static byte[] DeriveV1Key(string password)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var salt = "myssh-export-salt-v1"u8;
        var input = new byte[pw.Length + salt.Length];
        pw.CopyTo(input, 0);
        salt.CopyTo(input.AsSpan(pw.Length));
        try { return SHA256.HashData(input); }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private async Task<ImportResult> ImportV1Async(byte[] data, string password, CancellationToken ct)
    {
        TryDecodeV1(data, out var combined);
        var key = DeriveV1Key(password);
        byte[] plain;
        try
        {
            plain = FieldCipher.DecryptLegacy(key, combined.AsSpan(1));
        }
        catch (CryptographicException e)
        {
            throw new BackupException(BackupError.WrongPasswordOrCorrupt, "Wrong backup password, or the backup file is corrupted.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        List<JsonElement> records;
        try
        {
            records = JsonSerializer.Deserialize(plain, StorageJsonContext.Default.ListJsonElement) ?? [];
        }
        catch (JsonException e)
        {
            throw new BackupException(BackupError.InvalidFormat, $"The v1 backup payload is invalid: {e.Message}", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        var batch = new ImportBatch();
        var servers = new List<LegacyServer>();
        for (var i = 0; i < records.Count; i++)
        {
            string id = $"#{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            string? name = null;
            try
            {
                var json = records[i].Deserialize(StorageJsonContext.Default.LegacyServerJson) ?? throw new LegacyRecordException("Record is null.");
                id = json.Id ?? id;
                name = json.Name;
                var recordId = id;
                servers.Add(LegacyMapper.Decode(json, static s => s, null, w => batch.Warn(recordId, json.Name, w)));
            }
            catch (Exception e) when (e is LegacyRecordException or JsonException or InvalidOperationException)
            {
                batch.Fail(id, name, e.Message);
            }
        }
        LegacyMapper.Map(servers, batch);
        return await ImportWriter.WriteAsync(_db, _vault, batch, ImportMode.Overwrite, ImportSource.LegacyBackupV1, ct).ConfigureAwait(false);
    }

    // ---------------- helpers ----------------

    private string? TryDecrypt(string table, Guid id, string field, byte[]? blob, List<(UndecryptableRecord, byte[])> omitted)
    {
        if (blob is null)
            return null;
        try
        {
            return _vault.Decrypt(table, id, field, blob);
        }
        catch (CryptographicException e)
        {
            omitted.Add((new UndecryptableRecord(table, id, field, e.Message, DateTimeOffset.UtcNow, null), blob));
            return null;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream source, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxBackupBytes)
                throw new BackupException(BackupError.InvalidFormat, "The backup file is too large.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
