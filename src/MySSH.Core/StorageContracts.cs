using System.Text.Json.Serialization.Metadata;

namespace MySSH.Core;

/// <summary>Thrown when an operation needs secret material (or must encrypt it) while the vault is locked.</summary>
public sealed class VaultLockedException : InvalidOperationException
{
    public VaultLockedException()
        : base("The vault is locked. Unlock it (OS keystore or master password) before accessing or changing secrets.") { }

    public VaultLockedException(string message) : base(message) { }

    public VaultLockedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// A stored secret could not be decrypted (wrong master key, tampering, corruption). The record is kept untouched and
/// flagged; nothing is deleted or overwritten. Surface it to the user instead of dropping it.
/// </summary>
public sealed class SecretUnavailableException : InvalidOperationException
{
    public SecretUnavailableException(string table, Guid recordId, string field, Exception? innerException = null)
        : base($"The encrypted field '{field}' of {table} record {recordId} could not be decrypted. The record has been kept and flagged.", innerException)
    {
        Table = table;
        RecordId = recordId;
        Field = field;
    }

    public SecretUnavailableException() : base("A stored secret could not be decrypted.") { Table = ""; Field = ""; }

    public SecretUnavailableException(string message) : base(message) { Table = ""; Field = ""; }

    public SecretUnavailableException(string message, Exception innerException) : base(message, innerException) { Table = ""; Field = ""; }

    public string Table { get; }
    public Guid RecordId { get; }
    public string Field { get; }
}

/// <summary>Application settings: key → JSON value. Typed access goes through source-generated <see cref="JsonTypeInfo{T}"/> (NativeAOT).</summary>
public interface ISettingsStore
{
    Task<string?> GetJsonAsync(string key, CancellationToken ct = default);
    /// <summary><paramref name="json"/> must be a valid JSON document.</summary>
    Task SetJsonAsync(string key, string json, CancellationToken ct = default);
    Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct = default);
    Task<bool> RemoveAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default);
}
