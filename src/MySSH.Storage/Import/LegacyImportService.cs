using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MySSH.Storage;

public enum LegacyImportError { ServersFileMissing, KeyFileMissing, KeyFileInvalid, ServersFileUnreadable }

public sealed class LegacyImportException : Exception
{
    public LegacyImportException() { }
    public LegacyImportException(string message) : base(message) { }
    public LegacyImportException(string message, Exception innerException) : base(message, innerException) { }
    public LegacyImportException(LegacyImportError error, string message, Exception? innerException = null) : base(message, innerException) => Error = error;

    public LegacyImportError Error { get; }
}

/// <summary>What the legacy Tauri data directory contains.</summary>
public sealed record LegacyDetection(
    string Directory,
    bool ServersFileExists,
    bool KeyFileExists,
    bool KeyFileValid,
    /// <summary>Number of records in servers.json, null when the file is missing or unreadable.</summary>
    int? RecordCount,
    LegacyImportRecord? PreviousImport)
{
    public bool CanImport => ServersFileExists && KeyFileValid && RecordCount is not null;
    public bool AlreadyImported => PreviousImport is not null;
}

/// <summary>Stored in settings under <see cref="LegacyImportService.SettingsKey"/> after a successful import.</summary>
public sealed class LegacyImportRecord
{
    public DateTimeOffset CompletedAt { get; init; }
    public string SourceDirectory { get; init; } = "";
    public ImportResult? Result { get; init; }
}

/// <summary>
/// Imports <c>{legacy dir}/servers.json</c> + <c>.key</c> from the Tauri app (design §5.4). Legacy files are only read.
/// Idempotent: ids are kept (hosts) or derived deterministically (identities, proxies) and existing records are skipped.
/// </summary>
public sealed partial class LegacyImportService
{
    public const string SettingsKey = "import.legacy";
    public const string ServersFileName = "servers.json";
    public const string KeyFileName = ".key";

    private readonly StorageDatabase _db;
    private readonly Vault _vault;
    private readonly SettingsStore _settings;
    private readonly ILogger _logger;

    internal LegacyImportService(string legacyDirectory, StorageDatabase db, Vault vault, SettingsStore settings, ILogger logger)
    {
        LegacyDirectory = legacyDirectory;
        _db = db;
        _vault = vault;
        _settings = settings;
        _logger = logger;
    }

    public string LegacyDirectory { get; }
    private string ServersPath => Path.Combine(LegacyDirectory, ServersFileName);
    private string KeyPath => Path.Combine(LegacyDirectory, KeyFileName);

    public async Task<LegacyDetection> DetectAsync(CancellationToken ct = default)
    {
        var serversExists = File.Exists(ServersPath);
        var keyExists = File.Exists(KeyPath);
        var keyValid = keyExists && new FileInfo(KeyPath).Length == 32;
        int? count = null;
        if (serversExists)
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(ServersPath, ct).ConfigureAwait(false));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    count = doc.RootElement.EnumerateObject().Count();
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                count = null;
            }
        }
        return new LegacyDetection(LegacyDirectory, serversExists, keyExists, keyValid, count, await GetLastImportAsync(ct).ConfigureAwait(false));
    }

    public Task<LegacyImportRecord?> GetLastImportAsync(CancellationToken ct = default) =>
        _settings.GetAsync(SettingsKey, StorageJsonContext.Default.LegacyImportRecord, ct);

    /// <summary>
    /// Imports everything that decrypts; records that do not are reported in <see cref="ImportResult.Issues"/> and skipped.
    /// Requires an unlocked vault (secrets are re-encrypted with the MasterKey).
    /// </summary>
    /// <exception cref="LegacyImportException">servers.json or .key missing/invalid.</exception>
    /// <exception cref="MySSH.Core.VaultLockedException"/>
    public async Task<ImportResult> ImportAsync(CancellationToken ct = default)
    {
        _vault.EnsureUnlocked();
        if (!File.Exists(ServersPath))
            throw new LegacyImportException(LegacyImportError.ServersFileMissing, $"No legacy servers file at '{ServersPath}'.");
        if (!File.Exists(KeyPath))
            throw new LegacyImportException(LegacyImportError.KeyFileMissing,
                $"The legacy key file '{KeyPath}' is missing; the encrypted credentials in servers.json cannot be read.");

        var key = await File.ReadAllBytesAsync(KeyPath, ct).ConfigureAwait(false);
        try
        {
            if (key.Length != 32)
                throw new LegacyImportException(LegacyImportError.KeyFileInvalid,
                    $"The legacy key file '{KeyPath}' must contain exactly 32 bytes but has {key.Length}.");

            Dictionary<string, JsonElement> raw;
            try
            {
                raw = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(ServersPath, ct).ConfigureAwait(false),
                          StorageJsonContext.Default.DictionaryStringJsonElement)
                      ?? throw new JsonException("servers.json is empty.");
            }
            catch (JsonException e)
            {
                throw new LegacyImportException(LegacyImportError.ServersFileUnreadable, $"'{ServersPath}' is not a valid legacy servers file: {e.Message}", e);
            }

            var batch = new ImportBatch();
            var servers = new List<LegacyServer>();
            string Decrypt(string b64) => Encoding.UTF8.GetString(FieldCipher.DecryptLegacy(key, Convert.FromBase64String(b64)));
            foreach (var (mapKey, element) in raw.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                string? name = null;
                try
                {
                    var json = element.Deserialize(StorageJsonContext.Default.LegacyServerJson) ?? throw new LegacyRecordException("Record is null.");
                    name = json.Name;
                    json.Id ??= mapKey;
                    servers.Add(LegacyMapper.Decode(json, Decrypt, null, w => batch.Warn(mapKey, json.Name, w)));
                }
                catch (Exception e) when (e is LegacyRecordException or JsonException or InvalidOperationException)
                {
                    batch.Fail(mapKey, name, e.Message);
                    LogRecordSkipped(_logger, mapKey, e.Message);
                }
            }

            LegacyMapper.Map(servers, batch);
            var result = await ImportWriter.WriteAsync(_db, _vault, batch, ImportMode.SkipExisting, ImportSource.LegacyAppData, ct).ConfigureAwait(false);

            await _settings.SetAsync(SettingsKey, new LegacyImportRecord
            {
                CompletedAt = DateTimeOffset.UtcNow,
                SourceDirectory = LegacyDirectory,
                Result = result,
            }, StorageJsonContext.Default.LegacyImportRecord, ct).ConfigureAwait(false);
            LogImported(_logger, result.Hosts.Added, result.Hosts.Skipped, result.FailedRecords);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Legacy record {RecordId} skipped: {Reason}")]
    private static partial void LogRecordSkipped(ILogger logger, string recordId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Legacy import finished: {Added} hosts added, {Skipped} already present, {Failed} failed")]
    private static partial void LogImported(ILogger logger, int added, int skipped, int failed);
}
