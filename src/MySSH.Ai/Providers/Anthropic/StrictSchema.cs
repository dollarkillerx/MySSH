using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MySSH.Ai.Providers.Anthropic;

/// <summary>
/// Converts a tool catalog JSON Schema into the subset Claude's strict tool use accepts (structured outputs "JSON Schema
/// limitations"): numeric / string-length / pattern / array-size / unique / default keywords and unsupported string formats
/// are removed and summarized in that schema's <c>description</c> (e.g. "(1–2000)") so the model still sees them, and
/// every object gets <c>additionalProperties:false</c>. The full schema is still enforced later, in the tool layer.
/// Deterministic (same input → same bytes): the request prefix must stay byte-stable for caching and preserved thinking.
/// </summary>
internal static class StrictSchema
{
    // Formats strict mode supports (docs: structured outputs → JSON Schema limitations).
    static readonly HashSet<string> SupportedFormats = new(StringComparer.Ordinal)
    {
        "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid",
    };

    static readonly string[] SubschemaMaps = ["properties", "$defs", "definitions", "patternProperties"];
    static readonly string[] SubschemaLists = ["anyOf", "allOf", "oneOf", "prefixItems"];
    static readonly string[] SubschemaSingles = ["items", "not", "contains", "if", "then", "else"];

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The converted schema as sent (same encoder as the request body).</summary>
    public static string ToJson(JsonElement schema) => Convert(schema).ToJsonString(Relaxed);

    public static JsonNode Convert(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText()) ?? new JsonObject();
        if (node is JsonObject root)
            ConvertSchema(root);
        return node;
    }

    static void ConvertSchema(JsonObject s)
    {
        var notes = new List<string>();

        string? Raw(string key) => s[key] is JsonNode n ? n.ToJsonString(Relaxed) : null;

        // Numbers.
        var min = Raw("minimum");
        var max = Raw("maximum");
        if (min is not null && max is not null) notes.Add($"{min}–{max}");
        else if (min is not null) notes.Add($"≥ {min}");
        else if (max is not null) notes.Add($"≤ {max}");
        if (Raw("exclusiveMinimum") is { } xmin) notes.Add($"> {xmin}");
        if (Raw("exclusiveMaximum") is { } xmax) notes.Add($"< {xmax}");
        if (Raw("multipleOf") is { } mult) notes.Add($"multiple of {mult}");

        // Strings.
        var minLen = Raw("minLength");
        var maxLen = Raw("maxLength");
        if (minLen is not null && maxLen is not null) notes.Add($"{minLen}–{maxLen} characters");
        else if (maxLen is not null) notes.Add($"at most {maxLen} characters");
        else if (minLen is not null && minLen != "0") notes.Add(minLen == "1" ? "non-empty" : $"at least {minLen} characters");
        if (s["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var pattern)) notes.Add($"pattern {pattern}");
        if (s["format"] is JsonValue fv && fv.TryGetValue<string>(out var format) && !SupportedFormats.Contains(format))
        {
            notes.Add($"format {format}");
            s.Remove("format");
        }

        // Arrays.
        var minItems = Raw("minItems");
        var maxItems = Raw("maxItems");
        if (minItems is not null && maxItems is not null) notes.Add($"{minItems}–{maxItems} items");
        else if (maxItems is not null) notes.Add($"at most {maxItems} items");
        else if (minItems is not null && minItems != "0") notes.Add($"at least {minItems} items");
        if (s["uniqueItems"] is JsonValue uv && uv.TryGetValue<bool>(out var unique) && unique) notes.Add("unique items");

        if (Raw("default") is { } def) notes.Add($"default {def}");

        foreach (var k in (string[])["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength",
                     "maxLength", "pattern", "minItems", "maxItems", "uniqueItems", "default"])
            s.Remove(k);

        if (notes.Count > 0)
        {
            var note = "(" + string.Join("; ", notes) + ")";
            var existing = s["description"] is JsonValue dv && dv.TryGetValue<string>(out var d) ? d : null;
            s["description"] = string.IsNullOrWhiteSpace(existing) ? note : existing.TrimEnd() + " " + note;
        }

        // Strict mode: additionalProperties must be false on every object.
        if (IsObjectSchema(s))
            s["additionalProperties"] = false;

        foreach (var key in SubschemaMaps)
            if (s[key] is JsonObject map)
                foreach (var (_, sub) in map.ToList())
                    if (sub is JsonObject so) ConvertSchema(so);
        foreach (var key in SubschemaLists)
            if (s[key] is JsonArray list)
                foreach (var sub in list)
                    if (sub is JsonObject so) ConvertSchema(so);
        foreach (var key in SubschemaSingles)
        {
            if (s[key] is JsonObject so) ConvertSchema(so);
            else if (s[key] is JsonArray arr) // draft-04 tuple form of "items"
                foreach (var sub in arr)
                    if (sub is JsonObject so2) ConvertSchema(so2);
        }
    }

    static bool IsObjectSchema(JsonObject s)
    {
        if (s.ContainsKey("properties")) return true;
        return s["type"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var t) => t == "object",
            JsonArray a => a.Any(x => x is JsonValue xv && xv.TryGetValue<string>(out var t) && t == "object"),
            _ => false,
        };
    }
}
