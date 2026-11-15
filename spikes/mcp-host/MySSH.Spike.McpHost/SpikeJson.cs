using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;

namespace MySSH.Spike.McpHost;

public sealed record EndpointInfo(
    string Url, string Token, string Socket, int Pid, string HttpSessionMode, string Approval, bool Tray,
    double HostStartMs, double ProcessUptimeAtReadyMs, double WorkingSetBeforeHostMB, double WorkingSetAfterHostMB,
    string ClaudeCodeCommand);

/// <summary>
/// Source-generated metadata for every type our tools return / we serialize. Required for NativeAOT:
/// without it AIFunctionFactory throws "JsonTypeInfo metadata for type 'HostInfo[]' was not provided" at startup.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostInfo[]))]
[JsonSerializable(typeof(EndpointInfo))]
public sealed partial class SpikeJsonContext : JsonSerializerContext;

/// <summary>
/// Options handed to WithTools&lt;T&gt;(): our context first, then the SDK's (CallToolResult, primitives...).
/// GOTCHA: do NOT put this static initializer inside the partial context class. Static initializers across
/// partial declarations run in unspecified order; under NativeAOT `Default` was still null here, Combine()
/// silently dropped it, and startup failed with "JsonTypeInfo metadata for HostInfo[] was not provided".
/// </summary>
public static class SpikeJson
{
    public static JsonSerializerOptions ToolOptions { get; } = new(McpJsonUtilities.DefaultOptions)
    {
        TypeInfoResolver = JsonTypeInfoResolver.Combine(SpikeJsonContext.Default, McpJsonUtilities.DefaultOptions.TypeInfoResolver!),
    };
}
