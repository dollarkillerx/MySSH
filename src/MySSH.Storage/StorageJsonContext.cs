using System.Text.Json;
using System.Text.Json.Serialization;

namespace MySSH.Storage;

/// <summary>Source-generated JSON metadata for everything Storage serializes (NativeAOT: no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(VaultFileModel))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(LegacyServerJson))]
[JsonSerializable(typeof(LegacyPrivateKeyJson))]
[JsonSerializable(typeof(BackupPayload))]
[JsonSerializable(typeof(LegacyImportRecord))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
